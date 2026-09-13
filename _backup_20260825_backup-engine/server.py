#!/usr/bin/env python3
"""KARL'S LIGHT ACCESS —— UI 后端

只用标准库，不引入任何 Python 依赖（救援环境要尽可能少的活动部件）。
前端是 Chromium kiosk 加载的静态页面，通过 /api/* 拿数据。

阶段 1（已完成）：证明整条链路通——系统能起、X 能起、Chromium 能起、
后端能探到真实硬件、wimlib 在位。

阶段 2（进行中）：接功能。长任务统一走「提交作业 → 轮询进度」：
    POST /api/jobs            提交，返回作业 id
    GET  /api/jobs            列出全部
    GET  /api/jobs/<id>       轮询进度
    POST /api/jobs/<id>/cancel 请求取消（置标志位，作业自己退出）
只读的浏览走 GET /api/browse?dev=<分区名>&path=<相对路径>。

**写入纪律**：默认一切只读（见 _mount_ro）。整个后端只有 _mount_rw 一处
会以读写方式挂用户的卷，且只能从用户在 UI 上确认过的作业里调用。
"""

import json
import os
import re
import shutil
import stat
import subprocess
import threading
import time
import uuid
from datetime import datetime
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import urlparse, parse_qs

WWW_ROOT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "www")
READY_FLAG = "/run/kla-ui.ready"
PORT = 8760

# KLA 数据目录名。Windows 侧建在数据卷根目录下（默认 D:\KLA\），
# 这里遍历 NTFS 卷时靠这个名字认出它。两侧必须一致。
KLA_DIR = "KLA"
MOUNT_ROOT = "/mnt/kla"

# ESP 分区的 GPT 类型 GUID。lsblk 返回的 parttype 字段就靠它认出 ESP。
# 还原 ESP 时只覆盖 EFI\Microsoft\，保留 EFI\KLA\（risks.md R4b）。
ESP_GUID = "c12a7328-f81f-11d2-ba4b-00a0c93ec93b"
ESP_MNT = os.path.join(MOUNT_ROOT, "esp")
SHARE_MNT = os.path.join(MOUNT_ROOT, "shares")

# index.json 的格式版本，必须与 Windows 主程序一致（backup-design.md §4）
INDEX_SCHEMA_VERSION = 1

MIME = {
    ".html": "text/html; charset=utf-8",
    ".css": "text/css; charset=utf-8",
    ".js": "application/javascript; charset=utf-8",
    ".svg": "image/svg+xml",
    ".png": "image/png",
    # 启动画面的壁纸是 jpg。漏了它只会退到 octet-stream，浏览器嗅探之后
    # 通常照样显示——正因为"恰好能用"，这种漏项不会以报错的形式暴露出来。
    ".jpg": "image/jpeg",
    ".jpeg": "image/jpeg",
    ".ico": "image/x-icon",
}


def run(cmd, timeout=10):
    """跑一条命令，失败返回空串而不是抛异常——救援环境里任何工具都可能缺席。"""
    try:
        out = subprocess.run(
            cmd, capture_output=True, text=True, timeout=timeout, check=False
        )
        return out.stdout.strip()
    except (OSError, subprocess.SubprocessError):
        return ""


def list_disks():
    """枚举物理磁盘及其分区。lsblk 的 JSON 输出最稳，不用解析人类可读格式。"""
    raw = run(["lsblk", "-J", "-b", "-o",
               "NAME,SIZE,TYPE,FSTYPE,LABEL,MOUNTPOINT,MODEL,PARTTYPE"])
    if not raw:
        return []
    try:
        tree = json.loads(raw).get("blockdevices", [])
    except json.JSONDecodeError:
        return []

    def as_volume(node):
        return {
            "name": node.get("name"),
            "sizeBytes": node.get("size") or 0,
            "fstype": node.get("fstype"),
            "label": node.get("label"),
            "mountpoint": node.get("mountpoint"),
            "parttype": node.get("parttype"),
            # 认出我们自己的救援分区和 Windows 系统分区
            "isKlaRescue": (node.get("label") or "") == "KLA_RESCUE",
            "isWindows": (node.get("fstype") == "ntfs"),
        }

    disks = []
    for dev in tree:
        if dev.get("type") != "disk":
            continue
        children = dev.get("children") or []
        parts = [as_volume(c) for c in children]
        # 没有分区表、整盘就是一个文件系统的设备（superfloppy 布局）。
        # 很多 U 盘出厂就是这样，Windows 也认。只遍历 children 的话这种盘
        # 会列出来一块盘、底下一个卷都没有——用户看到的是"插了 U 盘但列表里没有"，
        # 而救援时目标卷十有八九就是那根 U 盘。
        # 判据是"有文件系统"而不是"没有 children"：分区表存在但分区还没建的空盘
        # 确实没有可用的卷，不该假装有。
        if not children and dev.get("fstype"):
            parts.append(dict(as_volume(dev), isWholeDisk=True))
        disks.append({
            "name": dev.get("name"),
            "model": (dev.get("model") or "").strip() or "未知型号",
            "sizeBytes": dev.get("size") or 0,
            "partitions": parts,
        })
    return disks


def network_status():
    """有没有网决定了「文件救援到网络位置」和浏览器能不能用。"""
    state = run(["nmcli", "-t", "-f", "STATE", "general"])
    conns = run(["nmcli", "-t", "-f", "NAME,TYPE,DEVICE", "connection", "show", "--active"])
    ifaces = []
    for line in conns.splitlines():
        bits = line.split(":")
        if len(bits) >= 3:
            ifaces.append({"name": bits[0], "type": bits[1], "device": bits[2]})
    return {"state": state or "unknown", "active": ifaces, "online": state == "connected"}


# ── Wi-Fi 连接（2026-08-25）─────────────────────────────────────────
# 背景：救援系统里 Intel/Realtek/MTK 的无线驱动和固件一直是齐的，但 UI 里
# 没有任何地方能输 Wi-Fi 密码——物理机用户插不了网线时，无线网卡看起来就
# 是「没被驱动」。这三个端点补上扫描/连接/断开。
#
# nmcli 的转义规则：-t 输出里 SSID 内的冒号是 "\:"。解析时还原。

def _nm_unescape(s):
    return s.replace("\\:", ":").replace("\\\\", "\\")


def wifi_scan():
    """扫描可见 Wi-Fi。首次调用顺带触发 rescan（nmcli 默认 auto）。"""
    if not shutil.which("nmcli"):
        return {"ok": False, "reason": "救援系统未内置 nmcli", "networks": []}
    # wifi 是否硬件可用：unavailable = 没无线网卡/被 rfkill 挡住
    hw = run(["nmcli", "-t", "-f", "WIFI", "general"], timeout=10).strip()
    if hw == "disabled":
        # 尝试软解除（硬开关挡住时解不了，提示用户看物理开关）
        run(["nmcli", "radio", "wifi", "on"], timeout=10)
        hw = run(["nmcli", "-t", "-f", "WIFI", "general"], timeout=10).strip()
        if hw != "enabled":
            return {"ok": False,
                    "reason": "无线被禁用——检查键盘上的 Wi-Fi 物理开关/飞行模式键",
                    "networks": []}
    out = run(["nmcli", "-t", "-f", "IN-USE,SSID,SIGNAL,SECURITY",
               "dev", "wifi", "list"], timeout=30)
    nets, seen = [], set()
    for line in out.splitlines():
        bits = line.split(":", 3)
        if len(bits) < 4 or not bits[1]:
            continue
        in_use, ssid, signal, sec = bits[0], _nm_unescape(bits[1]), bits[2], bits[3]
        key = ssid + "|" + sec
        if key in seen:  # 同名双频段 AP 去重，取信号强的那行（nmcli 按信号排序）
            continue
        seen.add(key)
        nets.append({"ssid": ssid,
                     "signal": int(signal) if signal.isdigit() else 0,
                     "security": sec or "开放",
                     "inUse": in_use == "*"})
    nets.sort(key=lambda n: (-n["inUse"], -n["signal"]))
    return {"ok": True, "networks": nets}


def wifi_connect(body):
    """连接 Wi-Fi。{ssid, password}。开放网络 password 可省略。"""
    ssid = (body.get("ssid") or "").strip()
    pwd = body.get("password") or ""
    # SSID 合法性：1-32 个字符，nmcli 拒绝空串；密码规则交给 wpa_supplicant
    # 校验——加密类型不同（WEP 5/13 vs WPA 8-63）长度要求不同，我们猜不准。
    if not ssid or len(ssid) > 32:
        return {"ok": False, "reason": "SSID 不合法"}, 400
    if not shutil.which("nmcli"):
        return {"ok": False, "reason": "救援系统未内置 nmcli"}, 400

    cmd = ["nmcli", "dev", "wifi", "connect", ssid]
    if pwd:
        cmd += ["password", pwd]
    proc = subprocess.run(cmd, capture_output=True, text=True, timeout=90)
    if proc.returncode != 0:
        err = (proc.stderr or proc.stdout or "").strip().splitlines()
        msg = err[-1] if err else "连接失败"
        # 常见错误翻译成人话
        if "Secrets were required" in msg or "no secrets" in msg.lower():
            msg = "密码错误"
        elif "No network with SSID" in msg:
            msg = "找不到这个网络（可能已离开覆盖范围），重新扫描试试"
        return {"ok": False, "reason": msg, "detail": "\n".join(err[-3:])}, 400
    return {"ok": True, "status": network_status()}, 200


def wifi_disconnect(body):
    """断开 Wi-Fi（按 SSID 或当前 wifi 设备）。"""
    ssid = (body.get("ssid") or "").strip()
    if ssid:
        run(["nmcli", "connection", "down", "id", ssid], timeout=20)
    else:
        # 没给 SSID 就断当前活动的 wifi 连接
        conns = run(["nmcli", "-t", "-f", "NAME,TYPE,DEVICE",
                     "connection", "show", "--active"], timeout=20)
        for line in conns.splitlines():
            bits = line.split(":")
            if len(bits) >= 3 and "wireless" in bits[1]:
                run(["nmcli", "connection", "down", "id", bits[0]], timeout=20)
    return {"ok": True, "status": network_status()}, 200


def tool_availability():
    """前端据此灰掉不可用的功能，而不是让用户点了才报错。"""
    tools = {
        "wimlib": "wimlib-imagex",
        "ntfs3g": "ntfs-3g",
        "smartctl": "smartctl",
        "memtester": "memtester",
        "gparted": "gparted",
        "testdisk": "testdisk",
        "chntpw": "chntpw",
        "chromium": "chromium",
        "cifs": "mount.cifs",
        "stressng": "stress-ng",
        "sensors": "sensors",
        "nmcli": "nmcli",
        "efibootmgr": "efibootmgr",
        "sshd": "sshd",
        "pcmanfm": "pcmanfm",
        "lxterminal": "lxterminal",
        "sgdisk": "sgdisk",
        "mkfs_vfat": "mkfs.vfat",
        "rsync": "rsync",
    }
    return {key: shutil.which(binary) is not None for key, binary in tools.items()}


def _mount_ro(dev, mnt):
    """只读挂载。失败返回 False，绝不抛异常。

    一律只读：救援环境里误写用户数据卷是不可接受的，写操作等到用户
    在 UI 上明确确认了某个动作再单独挂载。
    """
    try:
        os.makedirs(mnt, exist_ok=True)
    except OSError:
        return False
    if os.path.ismount(mnt):
        return True
    run(["mount", "-o", "ro", dev, mnt], timeout=20)
    return os.path.ismount(mnt)


def find_kla_store():
    """定位 KLA 数据目录（母盘 + 备份链 + index.json）。

    设计变更（2026-08-23）：数据不再放在 KLA_RESCUE 隐藏分区里，而是放在
    Windows 的数据卷上，默认 ``D:\\KLA\\``。原因是目标机器只有 238.5 GB，
    隐藏分区塞不下几十 GB 的母盘。见 docs/architecture.md「为什么把镜像放 D:」。

    所以不能只认 KLA_RESCUE，要遍历所有 NTFS 卷去找 KLA/ 目录——用户的数据卷
    盘符和卷标都是不确定的，只有目录名是我们自己定的。

    多个卷同时命中时按优先级择优（有 index.json 的优先、非 Windows 系统卷优先），
    而不是简单地取第一个：分区顺序上 C: 排在 D: 前面，用户若在 C: 根目录下也
    建过 KLA 文件夹，取第一个就会选错卷。
    """
    candidates = []
    for disk in list_disks():
        for part in disk.get("partitions", []):
            if part.get("fstype") == "ntfs" and part.get("name"):
                candidates.append(part)

    if not candidates:
        return {"found": False, "reason": "没有发现 NTFS 卷，无法定位 KLA 数据目录"}

    tried = []
    hits = []
    for part in candidates:
        dev = "/dev/" + part["name"]
        mnt = os.path.join(MOUNT_ROOT, part["name"])
        if not _mount_ro(dev, mnt):
            # Windows 快速启动/休眠会让卷处于 dirty 状态，ntfs-3g 可能拒绝挂载
            tried.append(f"{dev} 挂载失败")
            continue
        store = os.path.join(mnt, KLA_DIR)
        if not os.path.isdir(store):
            tried.append(f"{dev} 无 {KLA_DIR}/")
            continue

        has_index = os.path.isfile(os.path.join(store, "index.json"))
        is_system = os.path.isdir(os.path.join(mnt, "Windows", "System32"))
        hits.append({
            "found": True,
            "device": dev,
            "label": part.get("label"),
            "mountpoint": mnt,
            "storePath": store,
            # 排序键：有 index.json 的排前面，Windows 系统卷排后面
            "_rank": (0 if has_index else 1, 1 if is_system else 0),
        })

    if not hits:
        return {"found": False, "reason": "已挂载的 NTFS 卷上都没有 KLA 目录",
                "tried": tried}

    hits.sort(key=lambda h: h["_rank"])
    best = hits[0]
    best.pop("_rank", None)
    if len(hits) > 1:
        best["otherCandidates"] = [h["device"] for h in hits[1:]]
    return best


def read_backup_index():
    """读取 index.json —— Windows 主程序与 ACCESS 之间唯一的契约文件。

    同时返回数据卷剩余空间和 pending-restore 状态：
    - 剩余空间用于 R4（备份前空间检查），前端在发起还原/备份前要看这个数
    - pending-restore.json 是 Windows 侧「还原」按钮写下的交接文件，
      存在则说明这次开机是为了还原，UI 应直接跳到还原确认页
    """
    store = find_kla_store()
    if not store.get("found"):
        return {"found": False, "reason": store.get("reason"),
                "tried": store.get("tried", [])}

    result = {
        "found": False,
        "device": store["device"],
        "label": store.get("label"),
        "storePath": store["storePath"],
    }

    try:
        vfs = os.statvfs(store["mountpoint"])
        result["volumeFreeBytes"] = vfs.f_bavail * vfs.f_frsize
        result["volumeTotalBytes"] = vfs.f_blocks * vfs.f_frsize
    except OSError:
        pass

    pending = os.path.join(store["storePath"], "pending-restore.json")
    if os.path.isfile(pending):
        try:
            with open(pending, "r", encoding="utf-8") as fh:
                result["pendingRestore"] = json.load(fh)
        except (OSError, json.JSONDecodeError) as exc:
            result["pendingRestore"] = {"error": f"无法解析: {exc}"}

    index_path = os.path.join(store["storePath"], "index.json")
    if not os.path.isfile(index_path):
        result["reason"] = f"找到 {store['storePath']}，但里面没有 index.json"
        return result

    try:
        with open(index_path, "r", encoding="utf-8") as fh:
            data = json.load(fh)
    except (OSError, json.JSONDecodeError) as exc:
        result["reason"] = f"index.json 无法解析: {exc}"
        return result

    if data.get("schemaVersion") != INDEX_SCHEMA_VERSION:
        # 版本不认识就只读不改，绝不猜测格式（backup-design.md §4）
        result["reason"] = (f"index.json 版本 {data.get('schemaVersion')} "
                            f"不受支持（本机支持 {INDEX_SCHEMA_VERSION}）")
        result["index"] = data
        return result

    result["found"] = True
    result["index"] = data
    return result


def rescue_partition():
    """探测 KLA_RESCUE 分区本身是否在位。

    和数据目录是两回事：这个分区装的是当前正在跑的救援系统。
    诊断价值在于——如果找不到它，说明我们是从 U 盘/光盘启动的，
    而不是从硬盘上部署好的 KLA 启动的。
    """
    dev = run(["blkid", "-L", "KLA_RESCUE"])
    return {"found": bool(dev), "device": dev or None}



def system_info():
    model = run(["dmidecode", "-s", "system-product-name"]) or "未知"
    vendor = run(["dmidecode", "-s", "system-manufacturer"]) or ""
    kernel = run(["uname", "-r"])
    mem_kb = 0
    try:
        with open("/proc/meminfo", "r", encoding="utf-8") as fh:
            match = re.search(r"MemTotal:\s+(\d+)", fh.read())
            if match:
                mem_kb = int(match.group(1))
    except OSError:
        pass
    firmware = "UEFI" if os.path.isdir("/sys/firmware/efi") else "Legacy BIOS"
    secure_boot = "未知"
    try:
        for name in os.listdir("/sys/firmware/efi/efivars"):
            if name.startswith("SecureBoot-"):
                with open(f"/sys/firmware/efi/efivars/{name}", "rb") as fh:
                    # efivars 的前 4 字节是属性头，其后才是值；SecureBoot 值只有 1 字节
                    raw = fh.read()
                if raw:
                    secure_boot = "开启" if raw[-1] == 1 else "关闭"
                break
    except OSError:
        pass

    return {
        "machine": f"{vendor} {model}".strip(),
        "kernel": kernel,
        "memoryBytes": mem_kb * 1024,
        "firmware": firmware,
        "secureBoot": secure_boot,
    }


# 救援系统自身版本号：前端「关于/系统概况」和 splash 都从这里或 app.js 常量取，
# 两边改版本时记得同步。
KLA_VERSION = "1.2 Public Beta"


def sysinfo_overview():
    """系统概况：品牌/型号/序列号/CPU/GPU/RAM/存储/BIOS/版本。

    「系统概况」页的数据源。全部本地读取（DMI / proc / lspci / lsblk），
    只读操作，对被救援的磁盘没有任何影响。
    """
    vendor = run(["dmidecode", "-s", "system-manufacturer"]) or ""
    model = run(["dmidecode", "-s", "system-product-name"]) or ""
    serial = run(["dmidecode", "-s", "system-serial-number"]) or ""
    # 虚拟机/白牌机常给占位值，转成「—」别让用户以为出了 bug
    for placeholder in ("To Be Filled By O.E.M.", "To be filled by O.E.M.",
                        "None", "Not Specified", "System Product Name",
                        "Default string"):
        if serial == placeholder:
            serial = ""
        if model == placeholder:
            model = ""
        if vendor == placeholder:
            vendor = ""

    # CPU：型号取第一个 processor 块的 model name，核心数数 processor 行
    cpu_model, cores = "", 0
    try:
        with open("/proc/cpuinfo", "r", encoding="utf-8") as fh:
            text = fh.read()
        m = re.search(r"model name\s*:\s*(.+)", text)
        if m:
            cpu_model = m.group(1).strip()
        cores = text.count("processor\t:")
    except OSError:
        pass

    # GPU：lspci 里 VGA/3D/Display 三类控制器
    gpus = []
    for line in run(["lspci"]).splitlines():
        if any(k in line for k in ("VGA compatible controller",
                                   "3D controller", "Display controller")):
            # 行格式 "01:00.0 VGA compatible controller: NVIDIA GA106 ..."
            name = line.split(":", 2)[-1].strip()
            if name:
                gpus.append(name)

    # RAM
    mem_kb = 0
    try:
        with open("/proc/meminfo", "r", encoding="utf-8") as fh:
            m = re.search(r"MemTotal:\s+(\d+)", fh.read())
            if m:
                mem_kb = int(m.group(1))
    except OSError:
        pass

    # 存储（ROM）：物理磁盘列表。lsblk -d 只列磁盘本体不列分区。
    disks = []
    for line in run(["lsblk", "-d", "-n", "-o", "NAME,SIZE,MODEL,TYPE,ROTA"]).splitlines():
        bits = line.split(None, 4)
        if len(bits) < 5 or bits[3] != "disk":
            continue
        rota = bits[4].strip()
        disks.append({
            "name": "/dev/" + bits[0],
            "size": bits[1],
            "model": bits[2] if bits[2] not in ("", "?") else "—",
            # ROTA=1 机械盘 0 固态，用户看得懂这个区别
            "kind": "HDD" if rota == "1" else "SSD/闪存",
        })

    bios = " ".join(filter(None, [
        run(["dmidecode", "-s", "bios-vendor"]),
        run(["dmidecode", "-s", "bios-version"]),
    ]))
    bios_date = run(["dmidecode", "-s", "bios-release-date"])

    return {
        "ok": True,
        "vendor": vendor or "—",
        "model": model or "—",
        "serial": serial or "—",
        "cpu": {"model": cpu_model or "—", "cores": cores},
        "gpus": gpus or ["—"],
        "ramBytes": mem_kb * 1024,
        "disks": disks,
        "bios": (bios + (" (" + bios_date + ")" if bios_date else "")).strip() or "—",
        "version": KLA_VERSION,
        "serverNow": datetime.now().strftime("%Y-%m-%d %H:%M:%S"),
    }


# ---------------------------------------------------------------------------
# 作业（长任务）基础设施
#
# 文件救援、备份还原、恢复出厂都不是「点一下就返回」的操作——拷 50 GB 要几十
# 分钟。HTTP 请求不能一直挂在那儿，所以统一走「提交作业 → 轮询进度」。
#
# 只用 threading，不引入任何队列框架：救援环境里同一时刻只有一个人坐在一台
# 机器前面操作，并发量就是 1，复杂调度纯属负担。
# ---------------------------------------------------------------------------

JOBS = {}
JOBS_LOCK = threading.Lock()
# 同一时刻只允许一个作业。不是偷懒——两个作业同时写同一个卷，或者一个在还原
# 系统盘另一个在往它上面拷文件，后果是数据损坏，而这是救援工具最不能出的事。
JOB_EXCLUSIVE = True


class Job:
    """一个后台作业。字段直接就是给前端的 JSON，不额外做一层映射。"""

    def __init__(self, jtype, title):
        self.id = uuid.uuid4().hex[:12]
        self.type = jtype
        self.title = title
        self.state = "pending"        # pending/scanning/running/done/failed/cancelled
        self.message = ""
        self.done_bytes = 0
        self.total_bytes = 0
        self.done_files = 0
        self.total_files = 0
        self.current = ""
        # 单个文件读不出来不该让整个救援作业停下——恰恰相反，盘坏了才要救援，
        # 遇到坏块应该跳过并记下来，把能救的都救出来。所以错误是收集不是抛出。
        self.errors = []
        self.started_at = time.time()
        self.finished_at = None
        self.result = {}
        self.cancel = threading.Event()

    def snapshot(self):
        with JOBS_LOCK:
            return {
                "id": self.id,
                "type": self.type,
                "title": self.title,
                "state": self.state,
                "message": self.message,
                "doneBytes": self.done_bytes,
                "totalBytes": self.total_bytes,
                "doneFiles": self.done_files,
                "totalFiles": self.total_files,
                "current": self.current,
                # 错误可能上千条，全塞给前端没意义，给前 50 条和总数
                "errors": self.errors[:50],
                "errorCount": len(self.errors),
                "startedAt": self.started_at,
                "finishedAt": self.finished_at,
                "elapsedSeconds": round((self.finished_at or time.time()) - self.started_at, 1),
                "result": self.result,
            }

    def fail(self, msg):
        with JOBS_LOCK:
            self.state = "failed"
            self.message = msg
            self.finished_at = time.time()

    def finish(self, msg, **result):
        with JOBS_LOCK:
            self.state = "cancelled" if self.cancel.is_set() else "done"
            self.message = msg
            self.result = result
            self.finished_at = time.time()


def active_job():
    for job in JOBS.values():
        if job.state in ("pending", "scanning", "running"):
            return job
    return None


def start_job(jtype, title, target, args):
    """登记并起一个作业线程。返回 (job, 错误信息)。"""
    if JOB_EXCLUSIVE:
        busy = active_job()
        if busy is not None:
            return None, f"已有作业在跑（{busy.title}），请等它结束或取消它"
    job = Job(jtype, title)
    JOBS[job.id] = job

    def wrapper():
        try:
            target(job, **args)
        except Exception as exc:  # 作业线程里抛异常没人接，必须自己兜住
            job.fail(f"{type(exc).__name__}: {exc}")

    threading.Thread(target=wrapper, name=f"job-{job.id}", daemon=True).start()
    return job, None


# ---------------------------------------------------------------------------
# 卷与路径
# ---------------------------------------------------------------------------

# 分区名只允许字母数字。不允许 / 和 . ，路径穿越在这一步就断掉。
DEV_NAME_RE = re.compile(r"^[a-zA-Z0-9]+$")


def resolve_block_device(name):
    """把前端传来的分区名（nvme0n1p3）变成 /dev 路径，非法返回 None。

    两道都要：名字过白名单正则，且 /dev 下那个东西**确实是块设备**。
    只查名字的话，/dev 里随便一个字符设备或普通文件都能被当成卷去挂。
    """
    if not name or not DEV_NAME_RE.match(name):
        return None
    path = "/dev/" + name
    try:
        if not stat.S_ISBLK(os.stat(path).st_mode):
            return None
    except OSError:
        return None
    return path


def _mount_rw(dev, mnt):
    """可写挂载 —— **整个后端唯一会写用户卷的入口**。

    _mount_ro 的注释里写着「写操作等到用户在 UI 上明确确认了某个动作再单独
    挂载」，这里就是那个「单独挂载」。调用点必须是用户点过确认的路径。
    """
    try:
        os.makedirs(mnt, exist_ok=True)
    except OSError:
        return False
    if os.path.ismount(mnt):
        # 之前可能是只读挂上的，重挂成读写
        run(["mount", "-o", "remount,rw", mnt], timeout=20)
        return os.path.ismount(mnt)
    run(["mount", dev, mnt], timeout=30)
    return os.path.ismount(mnt)


def safe_join(root, rel):
    """把相对路径拼到挂载点下，确保结果没跑出去。返回 None 表示越界。

    两道检查缺一不可：
      1. normpath 之后比前缀 —— 挡住 ../../etc/shadow 这类
      2. realpath 之后**再比一次** —— 挡住符号链接逃逸。这条在救援场景里
         不是理论风险：Windows 卷上 `Documents and Settings` 本身就是指向
         `\\Users` 的联接，ntfs-3g 会把它呈现成符号链接。一路 realpath 下去
         完全可能走到挂载点外面，甚至走回救援系统自己的根文件系统。
    """
    root = os.path.realpath(root)
    target = os.path.normpath(os.path.join(root, (rel or "").lstrip("/")))
    if target != root and not target.startswith(root + os.sep):
        return None
    real = os.path.realpath(target)
    if real != root and not real.startswith(root + os.sep):
        return None
    return target


def mount_for_browse(dev_name):
    """按分区名只读挂载，返回 (挂载点, 错误信息)。"""
    dev = resolve_block_device(dev_name)
    if not dev:
        return None, f"非法或不存在的分区：{dev_name}"
    mnt = os.path.join(MOUNT_ROOT, dev_name)
    if not _mount_ro(dev, mnt):
        return None, (f"{dev} 挂载失败。NTFS 卷若是被 Windows 快速启动或休眠"
                      f"留在 dirty 状态，需要先在 Windows 里完全关机一次")
    return mnt, None


def browse(params):
    """列一个目录。只读，永远只读。"""
    dev_name = (params.get("dev") or [""])[0]
    rel = (params.get("path") or ["/"])[0]

    mnt, err = mount_for_browse(dev_name)
    if err:
        return {"ok": False, "reason": err}

    target = safe_join(mnt, rel)
    if target is None:
        return {"ok": False, "reason": "路径越界，已拒绝"}
    if not os.path.isdir(target):
        return {"ok": False, "reason": f"不是目录：{rel}"}

    entries = []
    try:
        with os.scandir(target) as it:
            for ent in it:
                try:
                    is_dir = ent.is_dir(follow_symlinks=False)
                    is_link = ent.is_symlink()
                    st = ent.stat(follow_symlinks=False)
                    size, mtime = st.st_size, st.st_mtime
                except OSError:
                    # 单个条目读不出来就标记它，不要让整个目录列不出来
                    is_dir = is_link = False
                    size, mtime = 0, 0
                entries.append({
                    "name": ent.name,
                    "isDir": is_dir,
                    "isLink": is_link,
                    "sizeBytes": 0 if is_dir else size,
                    "mtime": mtime,
                })
    except OSError as exc:
        return {"ok": False, "reason": f"无法读取目录：{exc}"}

    # 目录在前，再按名字排。中文名走 locale 排序意义不大，用码位序即可
    entries.sort(key=lambda e: (not e["isDir"], e["name"].lower()))
    return {"ok": True, "device": dev_name, "path": rel,
            "mountpoint": mnt, "entries": entries}


# ---------------------------------------------------------------------------
# 作业：文件救援（拷出）
# ---------------------------------------------------------------------------

def _scan(job, src_paths):
    """先数一遍有多少字节要拷，进度条才有分母。

    坏盘上这一遍本身可能很慢，所以作业状态先设成 scanning，让 UI 有话说。
    """
    total_b = total_f = 0
    for src in src_paths:
        if job.cancel.is_set():
            break
        if os.path.isfile(src):
            try:
                total_b += os.path.getsize(src); total_f += 1
            except OSError:
                pass
            continue
        for root, dirs, files in os.walk(src, followlinks=False):
            if job.cancel.is_set():
                break
            for name in files:
                try:
                    total_b += os.path.getsize(os.path.join(root, name))
                    total_f += 1
                except OSError:
                    total_f += 1          # 数不出大小也算一个文件
    return total_b, total_f


def _copy_file(job, src, dst):
    """拷一个文件，边拷边报进度，随时可取消。

    分块拷而不是 shutil.copyfile：一是要报进度，二是要能中途取消，
    三是坏块导致的 OSError 只影响这一个文件。
    """
    try:
        os.makedirs(os.path.dirname(dst), exist_ok=True)
        with open(src, "rb") as fi, open(dst, "wb") as fo:
            while True:
                if job.cancel.is_set():
                    return False
                chunk = fi.read(1024 * 1024)
                if not chunk:
                    break
                fo.write(chunk)
                with JOBS_LOCK:
                    job.done_bytes += len(chunk)
        try:
            st = os.stat(src)
            os.utime(dst, (st.st_atime, st.st_mtime))
        except OSError:
            pass          # 目标是 FAT32/exFAT 时时间戳精度有限，不算失败
        return True
    except OSError as exc:
        with JOBS_LOCK:
            job.errors.append({"path": src, "error": str(exc)})
        # 半截文件留在目标上比不留更危险——用户会以为救出来了
        try:
            os.unlink(dst)
        except OSError:
            pass
        return False


def job_copyout(job, src_dev, src_paths, dst_dev, dst_subdir):
    """把源卷上的若干路径拷到目标卷。源只读挂，目标读写挂。"""
    # 必须挡住字符串：JSON 里 "src_paths": "/Users" 是合法的，但下面
    # `for rel in src_paths` 会**逐个字符**去拼路径，得到一堆 /mnt/.../U、
    # /mnt/.../s ……然后报一串莫名其妙的"不存在"。
    if not isinstance(src_paths, list) or not all(isinstance(p, str) for p in src_paths):
        job.fail("src_paths 必须是字符串数组"); return

    src_mnt, err = mount_for_browse(src_dev)
    if err:
        job.fail(err); return

    dst = resolve_block_device(dst_dev)
    if not dst:
        job.fail(f"非法或不存在的目标分区：{dst_dev}"); return
    # 拷到自己身上：轻则原地复制占满卷，重则源目录套目标目录无限递归。
    if dst_dev == src_dev:
        job.fail("目标卷不能和源卷是同一个分区"); return

    dst_mnt = os.path.join(MOUNT_ROOT, dst_dev)
    if not _mount_rw(dst, dst_mnt):
        job.fail(f"目标卷 {dst} 无法以读写方式挂载"); return

    # 目标子目录也要过 safe_join：前端传个 ../.. 就写到救援系统根上去了
    dst_root = safe_join(dst_mnt, dst_subdir or "KLA-Rescued")
    if dst_root is None:
        job.fail("目标目录越界，已拒绝"); return

    abs_srcs = []
    for rel in src_paths:
        p = safe_join(src_mnt, rel)
        if p is None:
            with JOBS_LOCK:
                job.errors.append({"path": rel, "error": "路径越界，已跳过"})
            continue
        if os.path.exists(p):
            abs_srcs.append(p)
        else:
            with JOBS_LOCK:
                job.errors.append({"path": rel, "error": "不存在"})
    if not abs_srcs:
        job.fail("没有任何有效的源路径"); return

    # 去掉被别的选中项包住的路径。前端已经去过一遍，但 API 不能指望调用方守规矩：
    # 同时传 /Users 和 /Users/karl 不会拷错文件（内容一样，覆盖写而已），
    # 却会让 _scan 把同一批数据数两遍——进度条的分母因此虚高，
    # 用户看到的"总共要拷 12 GB"是假的，而救援时用户就是靠这个数字判断要等多久。
    # 排序后父目录必然排在自己所有子项前面（父路径是子路径的严格前缀）。
    abs_srcs.sort()
    deduped = []
    for p in abs_srcs:
        if any(p == q or p.startswith(q + os.sep) for q in deduped):
            continue
        deduped.append(p)
    abs_srcs = deduped

    with JOBS_LOCK:
        job.state = "scanning"
        job.message = "正在统计要拷贝的数据量…"
    total_b, total_f = _scan(job, abs_srcs)
    with JOBS_LOCK:
        job.total_bytes, job.total_files = total_b, total_f
        job.state = "running"
        job.message = "正在拷贝…"

    # 空间够不够先算清楚，别拷到一半才发现（risks.md R4：绝不半途失败）
    try:
        vfs = os.statvfs(dst_mnt)
        free = vfs.f_bavail * vfs.f_frsize
        if total_b > free:
            job.fail(f"目标卷空间不足：需要 {total_b // 2**20} MB，"
                     f"只剩 {free // 2**20} MB")
            return
    except OSError:
        pass

    copied = 0
    for src in abs_srcs:
        if job.cancel.is_set():
            break
        if os.path.isdir(src):
            # base 取父目录，relpath 才会把用户选中的那个文件夹名本身也带上，
            # 否则拷出来的是它的内容散在目标根下
            base = os.path.dirname(src.rstrip(os.sep))
            for root, dirs, files in os.walk(src, followlinks=False):
                if job.cancel.is_set():
                    break
                for name in files:
                    if job.cancel.is_set():
                        break
                    s = os.path.join(root, name)
                    d = os.path.join(dst_root, os.path.relpath(s, base))
                    with JOBS_LOCK:
                        job.current = os.path.relpath(s, src_mnt)
                    if _copy_file(job, s, d):
                        copied += 1
                    with JOBS_LOCK:
                        job.done_files += 1
        else:
            d = os.path.join(dst_root, os.path.basename(src))
            with JOBS_LOCK:
                job.current = os.path.relpath(src, src_mnt)
            if _copy_file(job, src, d):
                copied += 1
            with JOBS_LOCK:
                job.done_files += 1

    run(["sync"], timeout=120)
    job.finish(f"拷贝结束：成功 {copied} 个，失败 {len(job.errors)} 个",
               copiedFiles=copied, destination=dst_root)


# ---------------------------------------------------------------------------
# 作业：恢复出厂 / 从备份还原
# ---------------------------------------------------------------------------
# 共用辅助：找 ESP 分区、wimapply 子进程进度解析、ESP 重建。
# backup-design.md §6 是这节的契约来源。还原 ESP 时只覆盖 EFI\Microsoft\，
# 保留 EFI\KLA\——否则恢复出厂会把 KLA 自己的引导删掉（risks.md R4b）。

def find_esp_partition():
    """找 ESP 分区。返回 (device_path, partition_name) 或 (None, None)。

    ESP 是 GPT 上 parttype=c12a7328-... 的分区。lsblk 已经把 parttype 暴露
    在分区节点上，遍历即可。
    """
    for disk in list_disks():
        for part in disk.get("partitions", []):
            pt = (part.get("parttype") or "").lower()
            if pt == ESP_GUID:
                return "/dev/" + part["name"], part["name"]
    return None, None


def _wim_apply_subprocess(job, argv, label):
    """跑一条 wimlib-imagex 命令，把 stdout 行流式更新到 job.current。

    wimlib-imagex 的 --verbose 模式按行打印正在展开的文件路径。这些行
    本身就是用户最想看的「正在做什么」。wimlib 不汇报总字节数，所以
    进度条用 indet 模式滚动，前端不显示百分比。

    取消靠 terminate()——wimlib 在下一个文件之前会处理 SIGTERM，能干净退出。
    """
    with JOBS_LOCK:
        job.state = "running"
        job.message = label

    proc = subprocess.Popen(
        argv, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True
    )
    try:
        for line in proc.stdout:
            line = line.rstrip()
            if not line:
                continue
            if job.cancel.is_set():
                proc.terminate()
                break
            with JOBS_LOCK:
                job.current = line
                job.done_files += 1
    finally:
        proc.wait()
    return proc.returncode


def _wim_apply_to_dev(job, wim_path, target_dev, label="展开镜像"):
    """wimlib-imagex apply <wim> 1 /dev/<target> --check --verbose

    写块设备：wimlib 内部先 mkfs.ntfs 再展开，目标上的现有数据会被清空。
    适用于链上的 base（全量），以及 factory 的单层镜像。
    """
    target_mnt = os.path.join(MOUNT_ROOT, target_dev)
    if os.path.ismount(target_mnt):
        run(["umount", target_mnt], timeout=60)
    rc = _wim_apply_subprocess(
        job,
        ["wimlib-imagex", "apply", wim_path, "1", "/dev/" + target_dev,
         "--check", "--verbose"],
        label
    )
    if rc != 0 and not job.cancel.is_set():
        job.fail(f"wimapply 失败（返回码 {rc}，目标 {target_dev}）")
        return False
    return True


def _wim_apply_to_mnt(job, wim_path, target_mnt, label="应用增量"):
    """wimlib-imagex apply <wim> 1 <mountpoint> --check --verbose

    叠加到已挂载的卷上。适用于增量链的后续节点：base 已经清空+展开过，
    后续 wim 只把差异文件覆盖到挂载点。

    目标必须已 rw 挂载。调用方负责 mount 和 umount。
    """
    rc = _wim_apply_subprocess(
        job,
        ["wimlib-imagex", "apply", wim_path, "1", target_mnt,
         "--check", "--verbose"],
        label
    )
    if rc != 0 and not job.cancel.is_set():
        job.fail(f"wimapply 失败（返回码 {rc}，挂载点 {target_mnt}）")
        return False
    return True


def _restore_esp(job, esp_tar):
    """从 esp-factory.tar 重建 ESP：只覆盖 EFI\\Microsoft\\，保留 EFI\\KLA\\。

    实现 backup-design.md §6：删除 ESP 上的 EFI/Microsoft/，再 tar 解压
    esp-factory.tar 到 ESP 根。tar 是 Windows 侧部署时用
    ``tar -cf esp-factory.tar -C S:\\ .`` 生成的，内部路径相对 ESP 根。

    保留 EFI/KLA/ 是硬约束——否则恢复出厂会把 KLA 自己的引导删掉，
    下次就进不来了（risks.md R4b）。
    """
    if job.cancel.is_set():
        return False

    esp_dev, _ = find_esp_partition()
    if not esp_dev:
        with JOBS_LOCK:
            job.errors.append({"path": "ESP", "error": "找不到 ESP 分区，跳过引导重建"})
        return False

    os.makedirs(ESP_MNT, exist_ok=True)
    if os.path.ismount(ESP_MNT):
        run(["umount", ESP_MNT], timeout=30)

    # ESP 是 FAT，普通 mount 就能挂（ntfs-3g 不接 FAT）
    run(["mount", esp_dev, ESP_MNT], timeout=20)
    if not os.path.ismount(ESP_MNT):
        with JOBS_LOCK:
            job.errors.append({"path": "ESP", "error": f"无法挂载 ESP {esp_dev}"})
        return False

    ok = True
    try:
        with JOBS_LOCK:
            job.current = "清除 ESP 上的 EFI/Microsoft/"
        ms_dir = os.path.join(ESP_MNT, "EFI", "Microsoft")
        if os.path.isdir(ms_dir):
            # ignore_errors：个别文件被固件锁了删不掉，不要让整个 ESP 重建失败
            shutil.rmtree(ms_dir, ignore_errors=True)

        with JOBS_LOCK:
            job.current = f"解压 {os.path.basename(esp_tar)} 到 ESP"
        # tar -v 把解出来的文件逐行打到 stdout，用作进度反馈
        proc = subprocess.Popen(
            ["tar", "-xf", esp_tar, "-C", ESP_MNT, "-v"],
            stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True
        )
        try:
            for line in proc.stdout:
                line = line.rstrip()
                if not line:
                    continue
                if job.cancel.is_set():
                    proc.terminate()
                    break
                with JOBS_LOCK:
                    job.current = line
        finally:
            proc.wait()
        if proc.returncode != 0 and not job.cancel.is_set():
            with JOBS_LOCK:
                job.errors.append({"path": "ESP",
                                   "error": f"tar 解压失败（返回码 {proc.returncode}）"})
            ok = False
    finally:
        run(["umount", ESP_MNT], timeout=30)

    return ok


def _locate_factory_wim(store):
    """在 KLA 数据目录里找出厂母盘。

    首选部署时按约定写的 factory.wim（在 storePath 根目录下）。
    找不到才退回去翻 index.json，找 isFactoryBaseline=true 的条目——
    那是 Windows 主程序后来从备份管理台「设为出厂基准」时改的。
    """
    direct = os.path.join(store["storePath"], "factory.wim")
    if os.path.isfile(direct):
        return direct, "factory.wim"

    bi = read_backup_index()
    if bi.get("found") and bi.get("index"):
        for chain in bi["index"].get("chains", []):
            for entry in chain.get("entries", []):
                if entry.get("isFactoryBaseline"):
                    fpath = os.path.join(store["storePath"], entry.get("file", ""))
                    if os.path.isfile(fpath):
                        return fpath, entry["file"]
    return None, None


def job_factory(job, target_dev):
    """恢复出厂：用 D:\\KLA\\factory.wim 覆盖目标 C: 分区，并重建 ESP。

    factory.wim 是 Windows 侧部署 KLA 时用 ``wimlib-imagex capture --snapshot``
    捕获的「出厂母盘」。还原目标是同一批物理分区（GPT 分区 GUID 不变），
    所以捕获时 BCD 里的引用还原后依然有效，不需要重建 BCD——这一节论证
    见 backup-design.md §6。

    ESP 重建只覆盖 EFI\\Microsoft\\，保留 EFI\\KLA\\——否则恢复出厂会把 KLA
    自己的引导删掉，下次就进不来了（risks.md R4b）。
    """
    store = find_kla_store()
    if not store.get("found"):
        job.fail(store.get("reason") or "找不到 KLA 数据目录")
        return

    factory_wim, factory_label = _locate_factory_wim(store)
    if not factory_wim:
        job.fail("找不到出厂母盘 factory.wim。请先在 Windows 主程序里"
                 "做一次出厂基准备份（L0 范围，捕获整卷）")
        return

    if not resolve_block_device(target_dev):
        job.fail(f"非法或不存在的目标分区：{target_dev}")
        return

    esp_tar = os.path.join(store["storePath"], "esp-factory.tar")
    if not os.path.isfile(esp_tar):
        esp_tar = None
        with JOBS_LOCK:
            job.message = "警告：找不到 esp-factory.tar，将跳过引导重建"

    # 还原目标空间够不够先粗估——R4「绝不半途失败」的硬约束。
    # 不去解析 WIM 内部 total_bytes：解析要 fork wiminfo，而 wimlib 写块
    # 设备时本来就会先 mkfs（清空目标），空间不足会在 wimapply 自己报
    # ENOSPC。这里只对明显装不下的情况先挡一道。
    try:
        wim_size = os.path.getsize(factory_wim)
    except OSError:
        wim_size = 0
    target_part = None
    for disk in list_disks():
        for part in disk.get("partitions", []):
            if part.get("name") == target_dev:
                target_part = part
                break
    if target_part and wim_size * 3 > (target_part.get("sizeBytes") or 0):
        # LZX 经验压缩比 1:2.5~3。粗估，到 wimapply 时若空间不足会自然失败
        with JOBS_LOCK:
            job.message = (
                f"警告：目标 {target_dev} 大小 "
                f"{(target_part.get('sizeBytes') or 0) // 2**30} GB，"
                f"母盘展开后约需 {wim_size * 3 // 2**30} GB。"
                f"可能装不下，wimapply 会再报错一次。"
            )

    if not _wim_apply_to_dev(job, factory_wim, target_dev,
                             label=f"展开出厂母盘 {factory_label}"):
        return

    if esp_tar:
        with JOBS_LOCK:
            job.message = "正在重建引导分区…"
        _restore_esp(job, esp_tar)

    run(["sync"], timeout=300)
    job.finish(f"恢复出厂完成：已用 {factory_label} 覆盖 {target_dev}",
               factoryWim=factory_wim, target=target_dev)


def job_restore(job, entry_id, target_dev):
    """从备份还原：按 index.json 找到 entry_id 所属链，
    依次 wimapply base + 后续增量，最后重建 ESP。

    链上第一个 wim（base）写块设备——wimlib 自动 mkfs 清空目标；
    后续 wim（增量）写挂载点，叠加覆盖。这个顺序对应 backup-design.md §2
    里的链结构（base ← d001 ← d002 …）。

    ESP 重建只做一次（base 之后），用部署时的 esp-factory.tar。
    更精细方案是每个 entry 自带 esp.tar，但当前 index.json schema 没这字段。
    """
    store = find_kla_store()
    if not store.get("found"):
        job.fail(store.get("reason") or "找不到 KLA 数据目录")
        return

    bi = read_backup_index()
    if not bi.get("found"):
        job.fail(bi.get("reason") or "index.json 不可读")
        return
    index = bi["index"]

    # 找 entry_id 所属的 chain，以及它在链上的位置
    target_chain = None
    target_idx = -1
    for chain in index.get("chains", []):
        for i, entry in enumerate(chain.get("entries", [])):
            if entry.get("id") == entry_id:
                target_chain = chain
                target_idx = i
                break
        if target_chain is not None:
            break

    if target_chain is None:
        job.fail(f"在 index.json 里找不到还原点 {entry_id}")
        return

    # 校验链上从 base 到 target 的所有 wim 都存在（R5：链断裂要前置告警）
    chain_entries = target_chain["entries"]
    chain_files = []
    for i in range(target_idx + 1):
        e = chain_entries[i]
        fpath = os.path.join(store["storePath"], e.get("file", ""))
        if not os.path.isfile(fpath):
            job.fail(f"链上 {e.get('file')} 不存在，链已断裂（risks.md R5）")
            return
        chain_files.append((fpath, e))

    if not resolve_block_device(target_dev):
        job.fail(f"非法或不存在的目标分区：{target_dev}")
        return

    esp_tar = os.path.join(store["storePath"], "esp-factory.tar")
    if not os.path.isfile(esp_tar):
        esp_tar = None

    # base：写块设备（自动 mkfs 清空）
    base_path, base_entry = chain_files[0]
    with JOBS_LOCK:
        job.message = f"应用 base 镜像 {base_entry.get('file')}（1/{len(chain_files)}）"
    if not _wim_apply_to_dev(job, base_path, target_dev,
                             label=f"展开基础镜像 {base_entry.get('file')}"):
        return

    # ESP 重建（在 base 之后，增量之前——保证 ESP 上有干净 EFI/Microsoft）
    if esp_tar:
        with JOBS_LOCK:
            job.message = "正在重建引导分区…"
        _restore_esp(job, esp_tar)

    # 后续增量：挂载目标 rw，wimapply 到挂载点叠加
    target_mnt = os.path.join(MOUNT_ROOT, target_dev)
    if len(chain_files) > 1:
        target_dev_path = "/dev/" + target_dev
        if not _mount_rw(target_dev_path, target_mnt):
            job.fail(f"无法以读写方式挂载目标卷 {target_dev_path} 做增量应用")
            return
        try:
            for i, (wim_path, entry) in enumerate(chain_files[1:], start=2):
                if job.cancel.is_set():
                    break
                with JOBS_LOCK:
                    job.message = f"应用增量 {entry.get('file')}（{i}/{len(chain_files)}）"
                if not _wim_apply_to_mnt(
                        job, wim_path, target_mnt,
                        label=f"应用增量 {entry.get('file')}"):
                    return
        finally:
            run(["umount", target_mnt], timeout=120)

    run(["sync"], timeout=300)
    job.finish(f"还原完成：已应用 {len(chain_files)} 层镜像到 {target_dev}",
               entryId=entry_id, target=target_dev,
               chainId=target_chain.get("chainId"))


# ---------------------------------------------------------------------------
# 作业：修复启动
# ---------------------------------------------------------------------------

def job_bootfix(job):
    """修复启动：优先从 esp-factory.tar 重建 ESP 的 EFI/Microsoft/，
    其次用 efibootmgr 检查/创建 UEFI 启动项。

    与 factory/restore 里的 _restore_esp 相同实现，但独立成作业——
    用户只想修引导而不想还原整个系统时从这里进。
    """
    store = find_kla_store()
    esp_tar = None
    if store.get("found"):
        candidate = os.path.join(store["storePath"], "esp-factory.tar")
        if os.path.isfile(candidate):
            esp_tar = candidate

    if esp_tar:
        with JOBS_LOCK:
            job.message = "正在从出厂快照重建 ESP…"
        ok = _restore_esp(job, esp_tar)
        run(["sync"], timeout=60)
        if ok:
            job.finish("引导修复完成：已从 esp-factory.tar 重建 EFI/Microsoft/，"
                       "EFI/KLA/ 已保留",
                       espRestored=True, espSource=esp_tar)
        else:
            job.finish("引导修复完成（有告警）：ESP 重建遇到问题，见错误列表",
                       espRestored=False)
        return

    # 没有 esp-factory.tar：检查 ESP 上引导文件是否还在
    with JOBS_LOCK:
        job.message = "未找到 ESP 快照，检查现有引导文件…"

    esp_dev, _ = find_esp_partition()
    if not esp_dev:
        job.fail("找不到 ESP 分区，也无法找到 esp-factory.tar。"
                 "需要从 Windows 安装介质手动修复引导。")
        return

    os.makedirs(ESP_MNT, exist_ok=True)
    if not os.path.ismount(ESP_MNT):
        run(["mount", esp_dev, ESP_MNT], timeout=20)

    boot_efi = os.path.join(ESP_MNT, "EFI", "Microsoft", "Boot", "bootmgfw.efi")
    kla_efi = os.path.join(ESP_MNT, "EFI", "KLA", "grubx64.efi")
    has_windows_boot = os.path.isfile(boot_efi)
    has_kla_boot = os.path.isfile(kla_efi)
    run(["umount", ESP_MNT], timeout=30)

    with JOBS_LOCK:
        job.current = (
            f"ESP 检查：Windows 引导 {'在' if has_windows_boot else '缺失'}，"
            f"KLA 引导 {'在' if has_kla_boot else '缺失'}"
        )

    if not has_windows_boot and not has_kla_boot:
        job.fail("ESP 上既无 Windows 引导也无 KLA 引导，且无 esp-factory.tar 可还原。"
                 "需要从安装介质手动修复。")
        return

    # 尝试用 efibootmgr 检查/创建启动项
    out = run(["efibootmgr"], timeout=10)
    needs_windows_entry = has_windows_boot and "Boot0000" not in out and "Windows" not in out
    needs_kla_entry = has_kla_boot and "KLA" not in out

    created = []
    if needs_windows_entry:
        rc = run(["efibootmgr", "--create", "--label", "Windows Boot Manager",
                  "--disk", esp_dev, "--part", "1",
                  "--loader", "\\EFI\\Microsoft\\Boot\\bootmgfw.efi"], timeout=10)
        if "Boot" in (rc or ""):
            created.append("Windows Boot Manager")

    if needs_kla_entry:
        rc = run(["efibootmgr", "--create", "--label", "KLA",
                  "--disk", esp_dev, "--part", "1",
                  "--loader", "\\EFI\\KLA\\grubx64.efi"], timeout=10)
        if "Boot" in (rc or ""):
            created.append("KLA")

    run(["sync"], timeout=30)
    if created:
        job.finish(f"引导修复完成：已创建启动项 {', '.join(created)}。请重启验证。",
                   espPresent=True, createdEntries=created)
    else:
        job.finish("引导文件和启动项均在位。若仍无法启动，"
                   "可能是 BCD 配置损坏，需要从 Windows 安装介质执行 bcdboot 修复。",
                   espPresent=True, createdEntries=[])


# ---------------------------------------------------------------------------
# 作业：压力测试（stress-ng）
# ---------------------------------------------------------------------------

def job_stress(job, duration, mode):
    """运行 stress-ng 压力测试。duration 秒，mode 选 cpu/memory/disk/all。

    硬件诊断的一部分——系统不稳定时跑一下，能复现崩溃就说明是硬件问题。
    """
    secs = int(duration or 60)
    if secs < 1:
        secs = 60
    if secs > 3600:
        secs = 3600

    mode = mode or "cpu"
    if mode not in ("cpu", "memory", "disk", "all"):
        mode = "cpu"

    argv = ["stress-ng", "--timeout", str(secs), "--metrics-brief"]
    if mode == "cpu":
        argv.extend(["--cpu", "0"])
    elif mode == "memory":
        argv.extend(["--vm", "0", "--vm-bytes", "50%"])
    elif mode == "disk":
        argv.extend(["--io", "0", "--hdd", "0"])
    else:
        argv.extend(["--cpu", "0", "--vm", "0", "--vm-bytes", "50%", "--io", "0"])

    with JOBS_LOCK:
        job.state = "running"
        job.message = f"压力测试：{mode}，{secs} 秒"
        job.total_bytes = secs
        job.total_files = 1

    proc = subprocess.Popen(
        argv, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True
    )
    start = time.time()
    try:
        for line in proc.stdout:
            line = line.rstrip()
            if not line:
                continue
            if job.cancel.is_set():
                proc.terminate()
                break
            with JOBS_LOCK:
                job.current = line
                job.done_bytes = min(secs, int(time.time() - start))
    finally:
        proc.wait()

    run(["sync"], timeout=30)
    job.finish(f"压力测试完成（{mode}，{secs} 秒，退出码 {proc.returncode}）",
               mode=mode, duration=secs, exitCode=proc.returncode)


# ---------------------------------------------------------------------------
# 作业：内存测试（memtester，用户态）
# ---------------------------------------------------------------------------

def job_memtester(job, rounds):
    """运行 memtester 用户态内存测试。

    这是 MemTest86+ 的补充——MemTest86+ 需要重启到独立环境（GRUB 项），
    memtester 在救援环境内直接跑，不需要重启，快速排查用。
    """
    r = int(rounds or 1)
    if r < 1:
        r = 1
    if r > 20:
        r = 20

    # 取可用内存的 80% 做测试
    mem_free_kb = 0
    try:
        with open("/proc/meminfo", "r", encoding="utf-8") as fh:
            for line in fh:
                if line.startswith("MemFree:"):
                    mem_free_kb = int(line.split()[1])
                    break
    except (OSError, ValueError, IndexError):
        pass

    test_mb = max(16, int(mem_free_kb * 0.8 / 1024)) if mem_free_kb else 64
    argv = ["memtester", f"{test_mb}M", str(r)]

    with JOBS_LOCK:
        job.state = "running"
        job.message = f"内存测试：{test_mb} MB × {r} 轮"
        job.total_files = r

    proc = subprocess.Popen(
        argv, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True
    )
    try:
        for line in proc.stdout:
            line = line.rstrip()
            if not line:
                continue
            if job.cancel.is_set():
                proc.terminate()
                break
            with JOBS_LOCK:
                job.current = line
                if "Done" in line or "ok" in line.lower():
                    job.done_files += 1
    finally:
        proc.wait()

    ok = proc.returncode == 0
    job.finish(
        f"内存测试完成（{test_mb}MB × {r} 轮，{'通过' if ok else '发现错误'}）",
        testMB=test_mb, rounds=r, passed=ok, exitCode=proc.returncode
    )


# ---------------------------------------------------------------------------
# 作业：安装全新系统（把用户提供的 ISO 制作成可启动安装盘，整盘清空）
# ---------------------------------------------------------------------------
# 语义：不做离线 wimapply + 引导伪造——那条路在没有 bcdboot.exe 的 Linux 下
# 不可靠。走的是 Rufus「仅 FAT32 复制」同款标准流程：
#   · Windows ISO：目标盘做成单 FAT32 分区 → 复制 ISO 全部文件 →
#     install.wim 超 4 GiB 时用 wimlib-imagex split 拆成 .swm（安装器原生支持）。
#     重启后固件直接从该盘启动标准 Windows 安装器，用户点「现在安装」即可。
#   · Linux ISO：直接 dd 整盘刻录（hybrid ISO 的标准做法）。
# 安全：整个目标盘的数据会被清空。前端三层确认（红色警告 + 输入 ERASE +
# 最终确认），后端强制 confirm == "ERASE" 才动盘。

ISO_LOOP_MNT = "/mnt/kla-iso-loop"
INSTALL_TGT_MNT = "/mnt/kla-install-target"
# FAT32 单文件上限 4 GiB，拆分阈值留 300 MB 余量给卷描述符
WIM_SPLIT_MB = 3800


def _wait_dev(path, tries=20):
    """partprobe 后等设备节点出现。分区表刚写完 /dev/sdX1 可能要一两秒。"""
    for _ in range(tries):
        if os.path.exists(path):
            return True
        time.sleep(0.5)
    return False


def job_install(job, iso_dev, iso_path, target_disk, confirm, keep_kla_grub=True):
    """制作系统安装盘。iso_dev/iso_path 指向 ISO 文件所在卷和路径。
    keep_kla_grub=True 时，制作完成后把 KLA 引导项调回 BootOrder 最前——
    用户装完新系统后固件常常把新盘的 Windows Boot Manager 提到最前，
    KLA 菜单从此不再出现；这一步把它顶回来。"""
    if confirm != "ERASE":
        job.fail("缺少清盘确认（必须显式传 ERASE）。")
        return
    if not DEV_NAME_RE.match(iso_dev or ""):
        job.fail("无效的 ISO 所在分区名。")
        return
    if not DEV_NAME_RE.match(target_disk or ""):
        job.fail("无效的目标盘名。")
        return
    # 目标必须是整盘（/sys/block/ 下），把分区名传进来等于只清一个分区
    if not os.path.isdir(f"/sys/block/{target_disk}"):
        job.fail(f"目标 {target_disk} 不是整块磁盘。请选磁盘而不是分区。")
        return
    # 决不能把 ISO 源盘或救援系统自身当目标
    if iso_dev.startswith(target_disk) or target_disk == os.environ.get("KLA_DISK"):
        job.fail("目标盘不能是 ISO 所在盘。换一块盘，或先把 ISO 拷到 U 盘。")
        return

    src_mnt = os.path.join(MOUNT_ROOT, iso_dev)
    umounts = []

    def cleanup():
        for m in reversed(umounts):
            run(["umount", m], timeout=10)

    try:
        with JOBS_LOCK:
            job.state = "running"
            job.message = "挂载 ISO 所在分区…"
        if not _mount_ro(resolve_block_device(iso_dev), src_mnt):
            job.fail(f"无法挂载分区 {iso_dev} 读 ISO。")
            return
        umounts.append(src_mnt)

        iso_full = os.path.join(src_mnt, iso_path.lstrip("/"))
        if not os.path.isfile(iso_full):
            job.fail(f"找不到 ISO 文件：{iso_dev} 上的 {iso_path}")
            return

        os.makedirs(ISO_LOOP_MNT, exist_ok=True)
        with JOBS_LOCK:
            job.message = "挂载 ISO 镜像…"
        r = run(["mount", "-o", "loop,ro", iso_full, ISO_LOOP_MNT], timeout=30)
        if not r and os.system(f"mountpoint -q {ISO_LOOP_MNT}") != 0:
            # mount 失败输出为空且目录不是挂载点才算失败
            job.fail("无法挂载 ISO 镜像（文件损坏或格式不支持）。")
            return
        umounts.append(ISO_LOOP_MNT)

        is_windows = os.path.isfile(f"{ISO_LOOP_MNT}/sources/install.wim") or \
                     os.path.isfile(f"{ISO_LOOP_MNT}/sources/install.esd")
        is_linux = os.path.isdir(f"{ISO_LOOP_MNT}/casper") or \
                   os.path.isfile(f"{ISO_LOOP_MNT}/.disk/info")

        if is_windows:
            _install_windows(job, target_disk, umounts)
        elif is_linux:
            _install_linux_dd(job, target_disk, iso_full)
        else:
            job.fail("无法识别这个 ISO（既不是 Windows 安装镜像，也不是常见 "
                     "Linux live 镜像）。")

        # 制作完成 + 用户勾了保留 KLA：把 KLA 引导项顶回 BootOrder 第一。
        # 装完新系统重启时，固件/安装器常把新系统的 Boot Manager 排到最前，
        # KLA 菜单从此消失——这一步保证「每次开机先进 KLA 菜单」仍然成立。
        if keep_kla_grub and job.state == "done":
            _restore_kla_bootorder(job)
    finally:
        cleanup()


def _restore_kla_bootorder(job):
    """把 KLA 的 Boot#### 调回 BootOrder 第一位（efibootmgr -o）。

    失败静默（只更新 job.message）——安装盘本身已经做完，不能因为
    调顺序失败把整个作业标失败。
    """
    with JOBS_LOCK:
        job.message = "恢复 KLA 启动菜单优先级…"
    out = run(["efibootmgr"], timeout=15) or ""
    order = ""
    kla_num = ""
    entries = {}
    for line in out.splitlines():
        if line.startswith("BootOrder:"):
            order = line.split(":", 1)[1].strip().replace(" ", "")
        m = re.match(r"^(Boot\w{4})\*?\s+(.*)$", line)
        if m:
            entries[m.group(1)] = m.group(2)
    for num, name in entries.items():
        uname = name.upper()
        if "KARL" in uname and "ACCESS" in uname:
            kla_num = num[4:]          # "Boot0004" → "0004"
            break
    if not kla_num or not order:
        with JOBS_LOCK:
            job.message = "（未找到 KLA 引导项，跳过启动顺序调整）"
        return
    parts = [p for p in order.split(",") if p and p != kla_num]
    new_order = kla_num + ("," + ",".join(parts) if parts else "")
    if new_order == order:
        with JOBS_LOCK:
            job.message = "KLA 已在启动顺序第一位。"
        return
    run(["efibootmgr", "-o", new_order], timeout=15)
    with JOBS_LOCK:
        job.message = f"已把 KLA（{kla_num}）调回启动顺序第一位。"


def _install_windows(job, target_disk, umounts):
    """Windows ISO → 单 FAT32 分区安装盘（Rufus「仅 FAT32」同款）。"""
    dev = f"/dev/{target_disk}"
    part = f"{dev}1"

    with JOBS_LOCK:
        job.message = f"清空目标盘 {target_disk} 的分区表…"
    if run(["wipefs", "-a", dev], timeout=30) is None:
        run(["wipefs", "-a", dev], timeout=30)  # 一次失败再试，旧分区表有时要两遍
    run(["sgdisk", "-Z", dev], timeout=30)
    run(["sgdisk", "-n", "1:0:0", "-t", "1:0c01", dev], timeout=30)
    run(["partprobe", dev], timeout=30)
    run(["udevadm", "settle"], timeout=30)
    if not _wait_dev(part):
        job.fail(f"分区 {part} 没有出现，磁盘可能写保护或已损坏。")
        return

    with JOBS_LOCK:
        job.message = "格式化为 FAT32…"
    out = run(["mkfs.vfat", "-F", "32", "-n", "KLA_SETUP", part], timeout=120)
    if out is None:
        job.fail(f"mkfs.vfat 失败：{part}")
        return

    os.makedirs(INSTALL_TGT_MNT, exist_ok=True)
    if not _mount_rw(part, INSTALL_TGT_MNT):
        job.fail("无法挂载目标分区写入。")
        return
    umounts.append(INSTALL_TGT_MNT)

    # ---- 复制除大 install.wim 之外的所有文件（rsync 逐文件计进度）----
    big_wim = None
    for name in ("install.wim", "install.esd"):
        p = f"{ISO_LOOP_MNT}/sources/{name}"
        if os.path.isfile(p) and os.path.getsize(p) > WIM_SPLIT_MB * 1024 * 1024:
            big_wim = p
            break

    with JOBS_LOCK:
        job.message = "统计文件数…"
    excl = ["--exclude", "sources/install.wim", "--exclude", "sources/install.esd"] \
        if big_wim else []
    count_cmd = f"find {ISO_LOOP_MNT} -type f" + \
        (" | grep -v 'sources/install.wim$\\|sources/install.esd$'" if big_wim else "")
    total = sum(1 for _ in os.popen(count_cmd)) if count_cmd else 0
    extra = 1 if big_wim else 0
    with JOBS_LOCK:
        job.total_files = total + extra
        job.message = "复制安装文件…"

    proc = subprocess.Popen(
        ["rsync", "-a", "--info=name2"] + excl +
        [f"{ISO_LOOP_MNT}/", f"{INSTALL_TGT_MNT}/"],
        stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True,
    )
    for line in proc.stdout:
        if job.cancel.is_set():
            proc.terminate()
            job.fail("已取消（目标盘上是半成品，重新制作一遍即可）。")
            return
        with JOBS_LOCK:
            job.done_files += 1
            job.current = line.strip()[:120]
    rc = proc.wait()
    if rc != 0 and not job.cancel.is_set():
        job.fail(f"rsync 复制失败（退出码 {rc}）。目标盘可能已满或有坏块。")
        return

    # ---- 超大 install.wim → wimlib split 成 .swm（FAT32 单文件 4G 限制）----
    if big_wim:
        if job.cancel.is_set():
            job.fail("已取消。")
            return
        name = os.path.basename(big_wim)
        stem = name.rsplit(".", 1)[0]
        with JOBS_LOCK:
            job.message = f"{name} 超过 4 GB，正在拆分为 .swm（约需几分钟）…"
        argv = ["wimlib-imagex", "split", big_wim,
                f"{INSTALL_TGT_MNT}/sources/{stem}.swm", str(WIM_SPLIT_MB)]
        prc = subprocess.Popen(argv, stdout=subprocess.PIPE,
                               stderr=subprocess.STDOUT, text=True)
        for line in prc.stdout:
            if job.cancel.is_set():
                prc.terminate()
                job.fail("已取消（拆分中途停止，目标盘上是半成品）。")
                return
        if prc.wait() != 0:
            job.fail("wimlib 拆分 install.wim 失败。ISO 可能损坏。")
            return
        with JOBS_LOCK:
            job.done_files += 1

    run(["sync"], timeout=120)
    job.finish(
        f"安装介质制作完成。重启电脑，从 {target_disk} 这块盘启动，"
        "即进入标准 Windows 安装程序（选盘时注意别选错）。",
        targetDisk=target_disk, kind="windows",
    )


def _install_linux_dd(job, target_disk, iso_full):
    """Linux hybrid ISO → dd 整盘刻录，dd status=progress 解析字节进度。"""
    dev = f"/dev/{target_disk}"
    total = os.path.getsize(iso_full)
    with JOBS_LOCK:
        job.state = "running"
        job.total_bytes = total
        job.message = f"dd 刻录 {target_disk}…"
    proc = subprocess.Popen(
        ["dd", f"if={iso_full}", f"of={dev}", "bs=4M", "status=progress",
         "conv=fsync"],
        stdout=subprocess.DEVNULL, stderr=subprocess.PIPE, text=True,
    )
    m = re.compile(r"(\d+) bytes")
    for line in proc.stderr:
        if job.cancel.is_set():
            proc.terminate()
            job.fail("已取消（目标盘上是半成品）。")
            return
        g = m.search(line)
        if g:
            with JOBS_LOCK:
                job.done_bytes = int(g.group(1))
                job.current = f"{int(job.done_bytes * 100 // max(total, 1))}%"
    if proc.wait() != 0 and not job.cancel.is_set():
        job.fail("dd 刻录失败。目标盘可能写保护或损坏。")
        return
    run(["sync"], timeout=120)
    job.finish(
        f"安装介质制作完成。重启电脑，从 {target_disk} 启动即进入系统安装。",
        targetDisk=target_disk, kind="linux", bytes=total,
    )


# ---------------------------------------------------------------------------
# Linux 工具：针对用户硬盘上已安装 Linux 系统的常见适配性修复
# ---------------------------------------------------------------------------
# Linux 桌面在新硬件/老硬件上的适配性问题是真实的痛点：WiFi 固件没装上、
# 内核 modesetting 崩掉黑屏、更新中断导致 dpkg 死锁、NVIDIA 驱动打架……
# 这些问题在救援系统里 chroot 进目标系统就能修——每个修复是一组标准
# 命令（全部来自各发行版官方文档/Arch Wiki 的通行做法），用户选中自己
# 遇到的问题 → 选 Linux 分区 → 确认 → 后台执行。
#
# 命令在 chroot 里跑，apt 操作需要网络（提示用户先连网）。

LINUX_FIXES = {
    "dpkg-fix": {
        "title": "系统更新中断 / 软件包损坏修复",
        "desc": "最常见：升级或安装软件时断电/死机，之后 apt/dpkg 报错、装不了任何东西。",
        "steps": [
            ["dpkg", "--configure", "-a"],
            ["apt-get", "install", "-f", "-y"],
            ["apt-get", "update"],
        ],
    },
    "wifi-firmware": {
        "title": "WiFi / 蓝牙不能用（无线固件重装）",
        "desc": "无线网卡驱动装了但固件缺失（dmesg 里 firmware load failed）。重装 linux-firmware 并解除无线硬阻断。",
        "steps": [
            ["apt-get", "update"],
            ["apt-get", "install", "--reinstall", "-y", "linux-firmware",
             "wireless-regdb"],
            ["rfkill", "unblock", "all"],
        ],
    },
    "nomodeset": {
        "title": "开机黑屏 / 花屏（安全图形模式）",
        "desc": "NVIDIA/AMD 老卡或太新的卡驱动崩溃导致黑屏。给内核加 nomodeset 参数先让系统能亮起来。",
        "steps": [
            ["sed", "-i",
             "s/^GRUB_CMDLINE_LINUX_DEFAULT=.*/GRUB_CMDLINE_LINUX_DEFAULT=\"quiet splash nomodeset\"/",
             "/etc/default/grub"],
            ["grub-mkconfig", "-o", "/boot/grub/grub.cfg"],
        ],
    },
    "sound": {
        "title": "没有声音（音频栈重装）",
        "desc": "PipeWire/WirePlumber 音频服务损坏导致无声。重装音频组件并重置状态。",
        "steps": [
            ["apt-get", "update"],
            ["apt-get", "install", "--reinstall", "-y",
             "alsa-utils", "pipewire", "pipewire-pulse", "wireplumber"],
            ["rm", "-rf", "/var/lib/wireplumber"],
        ],
    },
    "nvidia-clean": {
        "title": "NVIDIA 驱动崩溃（回退开源驱动）",
        "desc": "专有驱动装挂了频繁死机/黑屏。卸载全部 NVIDIA 专有包，回退开源 nouveau。",
        "steps": [
            ["apt-get", "purge", "-y", "nvidia-driver-*", "libnvidia-*"],
            ["apt-get", "autoremove", "-y"],
            ["update-initramfs", "-u"],
        ],
    },
    "grub-rebuild": {
        "title": "重建 GRUB 引导（进不了系统）",
        "desc": "重装 Windows/换硬盘后 Linux 引导没了。重建 GRUB 到目标盘 ESP。",
        "steps": [
            ["grub-install", "--target=x86_64-efi",
             "--efi-directory=/boot/efi", "--bootloader-id=grub", "--recheck"],
            ["grub-mkconfig", "-o", "/boot/grub/grub.cfg"],
        ],
    },
    "passwd-root": {
        "title": "重置 Linux root 密码",
        "desc": "忘了 root/管理员密码。设为新密码后在目标系统里用它登录。",
        "steps": [],          # 特例：需要动态密码，在 job_linuxfix 里单独处理
    },
    "fsck-repair": {
        "title": "文件系统检查修复（磁盘错误）",
        "desc": "异常断电后系统报 file system error / readonly。卸载状态跑 fsck 修复。",
        "steps": [],          # 特例：不 chroot，直接 fsck 目标分区
    },
}

LINUXFS_MNT = "/mnt/kla-linuxfs"


def _chroot_mount_all(mnt, umounts):
    """挂 proc/sys/dev/devpts + resolv.conf（chroot 里 apt 要联网）。"""
    binds = [("proc", ["mount", "-t", "proc", "proc", f"{mnt}/proc"]),
             ("sys", ["mount", "-t", "sysfs", "sys", f"{mnt}/sys"]),
             ("dev", ["mount", "-o", "bind", "/dev", f"{mnt}/dev"]),
             ("devpts", ["mount", "-o", "bind", "/dev/pts", f"{mnt}/dev/pts"])]
    for _, argv in binds:
        try:
            subprocess.run(argv, timeout=15,
                           stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
            umounts.append(argv[-1])
        except (OSError, subprocess.TimeoutExpired):
            pass
    try:
        shutil.copy2("/etc/resolv.conf", f"{mnt}/etc/resolv.conf")
    except OSError:
        pass


def _chroot_umount_all(umounts):
    for m in reversed(umounts):
        run(["umount", m], timeout=10)


def job_linuxfix(job, fix_id, target_dev, confirm, new_password=None):
    """在目标 Linux 分区上执行一个修复。chroot 跑 LINUX_FIXES 里的命令。"""
    fix = LINUX_FIXES.get(fix_id)
    if not fix:
        job.fail(f"未知修复项：{fix_id}")
        return
    if not DEV_NAME_RE.match(target_dev or ""):
        job.fail("无效的分区名。")
        return
    if confirm != "YES":
        job.fail("缺少确认。")
        return

    src = resolve_block_device(target_dev)
    if not src:
        job.fail(f"无效的分区：{target_dev}")
        return

    os.makedirs(LINUXFS_MNT, exist_ok=True)

    # ── fsck 分支：不挂载，直接修 ──
    if fix_id == "fsck-repair":
        with JOBS_LOCK:
            job.state = "running"
            job.total_files = 4
            job.message = f"fsck 检查 {target_dev}（可能需要几分钟）…"
        proc = subprocess.Popen(
            ["fsck", "-y", src], stdout=subprocess.PIPE,
            stderr=subprocess.STDOUT, text=True)
        for line in proc.stdout:
            if job.cancel.is_set():
                proc.terminate()
                job.fail("已取消。")
                return
            with JOBS_LOCK:
                job.current = line.strip()[:120]
        rc = proc.wait()
        if rc == 0 or rc == 1:      # 0=无错 1=已修复
            job.finish(f"fsck 完成（退出码 {rc}，0=无错误 1=已自动修复）。"
                       "重启进系统看看还报不报错。", fix=fix_id)
        else:
            job.fail(f"fsck 退出码 {rc}（4=文件系统错误未修复，8=操作错误）。"
                     "硬盘可能正在坏，先抢救数据。")
        return

    # ── 常规分支：只读检测 → 读写挂载 → chroot 执行 ──
    with JOBS_LOCK:
        job.state = "running"
        job.message = f"挂载 {target_dev}…"
    if not _mount_rw(src, LINUXFS_MNT):
        # 只读再试一次：有的分区上次没干净卸载，readonly 能挂就能先诊断
        if not _mount_ro(src, LINUXFS_MNT):
            job.fail(f"无法挂载 {target_dev}。不是 ext4/btrfs 分区，或文件系统损坏严重。")
            return
        job.fail("分区处于不一致状态，先用「文件系统检查修复」跑一遍 fsck 再来。")
        run(["umount", LINUXFS_MNT], timeout=10)
        return

    umounts = [LINUXFS_MNT]
    inner = []
    try:
        osrel = os.path.join(LINUXFS_MNT, "etc", "os-release")
        if not os.path.isfile(osrel):
            job.fail("这个分区上没有 Linux 系统（找不到 /etc/os-release）。")
            return
        _chroot_mount_all(LINUXFS_MNT, inner)

        steps = fix["steps"]
        if fix_id == "passwd-root":
            pwd = new_password or ""
            if not re.match(r"^[!-~]{6,64}$", pwd):
                job.fail("需要提供 6-64 位新密码。")
                return
            steps = [["chpasswd"]]     # 密码走 stdin，不进命令行

        with JOBS_LOCK:
            job.total_files = len(steps)
            job.message = fix["title"] + "：执行 " + str(len(steps)) + " 步…"

        for i, argv in enumerate(steps):
            if job.cancel.is_set():
                job.fail("已取消（可能修到一半）。")
                return
            with JOBS_LOCK:
                job.current = "> " + " ".join(argv)[:110]
                job.message = f"第 {i+1}/{len(steps)} 步：{argv[0]}"
            proc = subprocess.Popen(
                ["chroot", LINUXFS_MNT] + argv,
                stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True)
            out_tail = []
            if fix_id == "passwd-root":
                proc.stdin.write(f"root:{new_password}\n")
                proc.stdin.flush()
                proc.stdin.close()
            for line in proc.stdout:
                out_tail.append(line.rstrip())
                out_tail = out_tail[-30:]
            rc = proc.wait()
            with JOBS_LOCK:
                job.done_files = i + 1
            if rc != 0:
                tail = "\n".join(out_tail[-12:])
                job.fail(f"第 {i+1} 步（{argv[0]}）失败，退出码 {rc}。\n{tail}")
                return

        run(["sync"], timeout=60)
        job.finish(f"{fix['title']} 完成。重启进系统验证效果。", fix=fix_id)
    finally:
        _chroot_umount_all(inner)
        run(["umount", LINUXFS_MNT], timeout=10)


JOB_TYPES = {
    "copyout": (job_copyout, "文件救援：拷出"),
    "factory": (job_factory, "恢复出厂"),
    "restore": (job_restore, "从备份还原"),
    "bootfix": (job_bootfix, "修复启动"),
    "stress": (job_stress, "压力测试"),
    "memtester": (job_memtester, "内存测试"),
    "install": (job_install, "安装全新系统"),
    "linuxfix": (job_linuxfix, "Linux 修复"),
}


def submit_job(body):
    jtype = body.get("type")
    spec = JOB_TYPES.get(jtype)
    if not spec:
        return {"ok": False, "reason": f"未知作业类型：{jtype}"}, 400
    target, title = spec
    args = body.get("args") or {}
    if not isinstance(args, dict):
        return {"ok": False, "reason": "args 必须是对象"}, 400
    # 只把作业函数声明了的参数传进去，前端多塞的字段一律丢弃
    allowed = target.__code__.co_varnames[1:target.__code__.co_argcount]
    missing = [k for k in allowed if k not in args]
    if missing:
        return {"ok": False, "reason": f"缺少参数：{', '.join(missing)}"}, 400
    job, err = start_job(jtype, title, target, {k: args[k] for k in allowed})
    if err:
        return {"ok": False, "reason": err}, 409
    return {"ok": True, "job": job.snapshot()}, 200


def list_jobs():
    return {"jobs": [j.snapshot() for j in JOBS.values()]}


# ---------------------------------------------------------------------------
# 图形程序启动（gparted / chromium / testdisk / 网络编辑器 / chntpw 等）
# ---------------------------------------------------------------------------
# 白名单。前端只传 app 名，后端查表拿 argv——绝不能让前端直接传命令行，
# 那等于开了个远程 shell。
LAUNCH_APPS = {
    "gparted":      ["gparted"],
    "chromium":     ["chromium", "--no-sandbox", "--no-first-run", "--disable-gpu"],
    "nm-editor":    ["nm-connection-editor"],
    "testdisk":     ["lxterminal", "-e", "testdisk"],
    "photorec":     ["lxterminal", "-e", "photorec"],
    "terminal":     ["lxterminal"],
    "files":        ["pcmanfm"],
}

# 网络代理状态文件（tmpfs，重启即清零）。设置为空字符串 = 关闭代理。
PROXY_FILE = "/run/kla-proxy"


def _read_proxy():
    try:
        with open(PROXY_FILE, "r", encoding="utf-8") as fh:
            v = fh.read().strip()
            return v if re.match(r"^[!-~]{3,128}$", v) else ""
    except OSError:
        return ""


def _apply_proxy_env(proxy):
    """把代理写进 /etc/environment 和 apt 配置——救援环境里的 curl/wget/
    apt/chromium 都从这里吃配置。用户场景：直连访问不了 GitHub 等站点，
    借路由器上已有的代理应急。"""
    pairs = []
    for key in ("http_proxy", "https_proxy", "HTTP_PROXY", "HTTPS_PROXY"):
        pairs.append(f"{key}={proxy}")
    for key in ("no_proxy", "NO_PROXY"):
        pairs.append(f"{key}=localhost,127.0.0.1,::1")
    try:
        with open("/etc/environment", "w", encoding="utf-8") as fh:
            fh.write("\n".join(pairs) + "\n")
    except OSError:
        pass
    aptconf = "/etc/apt/apt.conf.d/95kla-proxy"
    try:
        if proxy:
            with open(aptconf, "w", encoding="utf-8") as fh:
                fh.write(f'Acquire::http::Proxy "http://{proxy}";\n'
                         f'Acquire::https::Proxy "http://{proxy}";\n')
        elif os.path.isfile(aptconf):
            os.remove(aptconf)
    except OSError:
        pass


def proxy_status():
    return {"ok": True, "proxy": _read_proxy()}


def proxy_control(body):
    proxy = (body.get("proxy") or "").strip()
    if proxy and not re.match(r"^[!-~]{3,128}$", proxy):
        return {"ok": False,
                "reason": "代理地址只能是字母数字符号（host:port 格式）"}, 400
    try:
        if proxy:
            with open(PROXY_FILE, "w", encoding="utf-8") as fh:
                fh.write(proxy)
        elif os.path.isfile(PROXY_FILE):
            os.remove(PROXY_FILE)
    except OSError as exc:
        return {"ok": False, "reason": f"写入失败：{exc}"}, 500
    _apply_proxy_env(proxy)
    return {"ok": True, "proxy": proxy}, 200

# chntpw 的用户名只允许这些字符——Windows 用户名本来就可以更宽，但在这里
# 把它收窄到安全集合，避免 shell 注入（chntpw 通过 bash -c 调用）。
USERNAME_RE = re.compile(r"^[A-Za-z0-9._ -]{1,64}$")


def launch_app(body):
    """启动一个外部图形程序。

    救援环境里这些程序本身就能跑，UI 只是替用户点一下「打开」。
    进程脱离后端 fork（start_new_session），后端不跟踪它的生命周期——
    用户在图形窗口里自己关掉即可。

    chntpw 是特例：需要先挂载 Windows 分区、定位 SAM 文件，再在终端里
    启动 chntpw 的交互菜单。
    """
    app = body.get("app")
    dev = body.get("dev")        # 仅 chntpw 用
    user = body.get("user")     # 仅 chntpw 用

    env = os.environ.copy()
    env["DISPLAY"] = env.get("DISPLAY") or ":0"
    env["XAUTHORITY"] = env.get("XAUTHORITY") or ""

    if app == "chntpw":
        if not dev or not DEV_NAME_RE.match(dev):
            return {"ok": False, "reason": "需要指定有效的分区名"}, 400
        path = resolve_block_device(dev)
        if not path:
            return {"ok": False, "reason": f"无效的分区：{dev}"}, 400
        mnt = os.path.join(MOUNT_ROOT, dev)
        if not _mount_rw(path, mnt):
            return {"ok": False, "reason":
                    "无法以读写方式挂载该分区。"
                    "NTFS 卷若被 Windows 快速启动留在 dirty 状态需要先正常关机一次"}, 500
        sam = os.path.join(mnt, "Windows", "System32", "config", "SAM")
        if not os.path.isfile(sam):
            return {"ok": False, "reason":
                    "找不到 Windows/System32/config/SAM，"
                    "选的分区可能不是 Windows 系统卷"}, 500

        # chntpw -u <user> SAM 走交互菜单；不传 user 就用 chntpw -i 列全部用户
        # SAM 路径和用户名都经过白名单正则校验，不会注入
        if user:
            if not USERNAME_RE.match(user):
                return {"ok": False, "reason": "用户名含不允许的字符"}, 400
            cmd = f"chntpw -u {user!r} {sam!r}"
        else:
            cmd = f"chntpw -i {sam!r}"
        # lxterminal -e bash -c "cmd; read" —— chntpw 退出后终端不立即关闭
        argv = ["lxterminal", "-e", "bash", "-c",
                f"{cmd}; echo; echo 'chntpw 已退出，按回车关闭窗口'; read line"]
    else:
        argv = LAUNCH_APPS.get(app)
        if not argv:
            return {"ok": False, "reason": f"不允许的程序：{app}"}, 400
        if not shutil.which(argv[0]):
            return {"ok": False, "reason": f"{argv[0]} 未安装"}, 400
        # 设置了代理时，浏览器带上 --proxy-server（chromium 不读 /etc/environment）
        if app == "chromium":
            proxy = _read_proxy()
            if proxy:
                argv = argv + [f"--proxy-server=http://{proxy}"]

    try:
        subprocess.Popen(argv, env=env,
                         stdin=subprocess.DEVNULL,
                         stdout=subprocess.DEVNULL,
                         stderr=subprocess.DEVNULL,
                         start_new_session=True)
    except OSError as exc:
        return {"ok": False, "reason": str(exc)}, 500
    return {"ok": True, "app": app}, 200


# ---------------------------------------------------------------------------
# 硬件诊断：SMART + 传感器
# ---------------------------------------------------------------------------

def smart_data(params):
    """读取硬盘 SMART 数据。"""
    dev = (params.get("dev") or [""])[0]
    if not dev or not DEV_NAME_RE.match(dev):
        return {"ok": False, "reason": "无效的设备名"}

    path = "/dev/" + dev
    try:
        if not stat.S_ISBLK(os.stat(path).st_mode):
            return {"ok": False, "reason": f"{path} 不是块设备"}
    except OSError:
        return {"ok": False, "reason": f"找不到 {path}"}

    # smartctl 自动检测设备类型，不传 -d 让它自己判断
    out = run(["smartctl", "-a", path], timeout=15)
    return {"ok": bool(out), "device": dev, "output": out or "（无输出，可能设备不支持 SMART）"}


def sensors_data():
    """读取 lm-sensors 输出。"""
    out = run(["sensors"], timeout=10)
    return {"ok": bool(out), "output": out or "（sensors 未安装或无传感器）"}


# ---------------------------------------------------------------------------
# SMB/CIFS 网络共享挂载
# ---------------------------------------------------------------------------

def list_shares():
    """列出已挂载的 SMB 共享。"""
    shares = []
    if os.path.isdir(SHARE_MNT):
        for name in os.listdir(SHARE_MNT):
            mnt = os.path.join(SHARE_MNT, name)
            if os.path.ismount(mnt):
                try:
                    vfs = os.statvfs(mnt)
                    free = vfs.f_bavail * vfs.f_frsize
                    total = vfs.f_blocks * vfs.f_frsize
                except OSError:
                    free = total = 0
                shares.append({
                    "name": name,
                    "mountpoint": mnt,
                    "freeBytes": free,
                    "totalBytes": total,
                })
    return {"shares": shares}


# 共享名只允许字母数字下划线横线——它会变成挂载点名，不能含路径分隔符
SHARE_NAME_RE = re.compile(r"^[A-Za-z0-9_-]{1,32}$")

# 主机名/IPv4：不允许路径分隔符、空格、反斜杠。不校验的话 host="bad/host"
# 会让 mount.cifs 把 UNC 解析坏，报个晦涩的 "could not resolve address"，
# 前端拿到 500 看不出真因。
HOST_RE = re.compile(r"^[A-Za-z0-9._:-]{1,255}$")


def mount_share(body):
    """挂载 SMB/CIFS 共享。"""
    host = (body.get("host") or "").strip()
    share = (body.get("share") or "").strip()
    user = (body.get("user") or "").strip()
    password = body.get("password") or ""
    name = (body.get("name") or share).strip()

    if not host or not share:
        return {"ok": False, "reason": "需要填写主机和共享名"}, 400
    if not HOST_RE.match(host):
        return {"ok": False, "reason": "主机名含不允许的字符"}, 400
    if not SHARE_NAME_RE.match(name):
        return {"ok": False, "reason": "挂载点名称只允许字母数字、下划线、横线（最多 32 字符）"}, 400

    mnt = os.path.join(SHARE_MNT, name)
    os.makedirs(mnt, exist_ok=True)
    if os.path.ismount(mnt):
        return {"ok": False, "reason": f"{name} 已挂载，请先卸载"}, 409

    unc = f"//{host}/{share}"
    opts = []
    if user:
        opts.append(f"username={user}")
        opts.append(f"password={password}")
    else:
        opts.append("guest")
    opts.extend(["iocharset=utf8", "vers=3.0", "uid=0", "gid=0"])

    argv = ["mount", "-t", "cifs", unc, mnt, "-o", ",".join(opts)]
    rc = subprocess.run(argv, capture_output=True, text=True, timeout=30, check=False)
    if rc.returncode != 0:
        try:
            os.rmdir(mnt)
        except OSError:
            pass
        return {"ok": False,
                "reason": rc.stderr.strip() or f"挂载失败（返回码 {rc.returncode}）"}, 500

    return {"ok": True, "name": name, "mountpoint": mnt}, 200


def unmount_share(name):
    """卸载 SMB 共享。"""
    if not SHARE_NAME_RE.match(name):
        return {"ok": False, "reason": "无效的共享名"}, 400
    mnt = os.path.join(SHARE_MNT, name)
    if not os.path.ismount(mnt):
        return {"ok": False, "reason": f"{name} 未挂载"}, 404

    rc = run(["umount", mnt], timeout=20)
    if os.path.ismount(mnt):
        return {"ok": False, "reason": "卸载失败（可能忙）"}, 500
    try:
        os.rmdir(mnt)
    except OSError:
        pass
    return {"ok": True, "name": name}, 200


# ---------------------------------------------------------------------------
# 电源操作
# ---------------------------------------------------------------------------

def power_action(body):
    """重启或关机。救援环境里跑 systemd，直接调 systemctl 即可。"""
    action = body.get("action")
    if action == "reboot":
        argv = ["systemctl", "reboot"]
    elif action == "shutdown":
        argv = ["systemctl", "poweroff"]
    else:
        return {"ok": False, "reason": "未知操作，支持 reboot / shutdown"}, 400

    try:
        # start_new_session：systemctl 会 fork 出实际执行的进程，
        # 不让它被后端的进程组拖住
        subprocess.Popen(argv, start_new_session=True)
    except OSError as exc:
        return {"ok": False, "reason": str(exc)}, 500
    return {"ok": True, "action": action}, 200


# ---------------------------------------------------------------------------
# 显卡诊断：型号/驱动（lspci + sysfs）+ 连接器状态 + 内核报错（dmesg）
# ---------------------------------------------------------------------------
# 不依赖 GUI 的 glxinfo——救援环境没有 GPU 固件时 GL 全是 llvmpipe 软渲染，
# glxinfo 的"显存"数据没有意义。内核态信息（lspci/sysfs/dmesg）才是
# "显卡到底是哪张、驱动认没认、有没有报错"的可靠来源。

PCI_GPU_CLASSES = ('"VGA compatible controller', '"3D controller',
                   '"Display controller')


def _read_text(path):
    try:
        with open(path, "r", encoding="utf-8") as fh:
            return fh.read().strip()
    except OSError:
        return ""


def gpu_data():
    """显卡诊断数据。cards[] 每张卡：slot/型号/PCI ID/驱动/VRAM/连接器。"""
    cards = []

    # lspci -mm 的机器可读格式：
    #   0000:01:00.0 "VGA compatible controller [0300]" "NVIDIA ... [10de:2504]" ...
    out = run(["lspci", "-nn", "-mm"], timeout=10) or ""
    for line in out.splitlines():
        if not any(k in line for k in PCI_GPU_CLASSES):
            continue
        bits = line.split('"')
        if len(bits) < 4:
            continue
        slot = line.split()[0]
        # bits: [slot,'Class 0300 [0300]',',', 'NVIDIA GA106 [10de:2504]', ...]
        desc = bits[3]
        pci_id = ""
        m = re.search(r"\[([0-9a-f]{4}:[0-9a-f]{4})\]$", desc)
        if m:
            pci_id = m.group(1)
            desc = desc[:m.start()].strip()

        # 驱动：/sys/bus/pci/devices/<slot>/driver 符号链接的 basename
        drv = ""
        try:
            drv = os.path.basename(os.readlink(
                f"/sys/bus/pci/devices/{slot}/driver"))
        except OSError:
            pass

        # DRM card：/sys/class/drm/cardN/device → ../devices/pci…/<slot>
        card_name, vram, vram_used = "", 0, 0
        connectors = []
        for card in sorted(os.listdir("/sys/class/drm")):
            if not card.startswith("card") or "-" in card:
                continue
            try:
                devpath = os.path.realpath(f"/sys/class/drm/{card}/device")
            except OSError:
                continue
            if devpath.endswith(f"/{slot}"):
                card_name = card
                vram = int(_read_text(f"/sys/class/drm/{card}/device/"
                                      "mem_info_vram_total") or 0)
                vram_used = int(_read_text(f"/sys/class/drm/{card}/device/"
                                           "mem_info_vram_used") or 0)
                # 连接器：cardN-HDMI-A-1 等
                for ent in sorted(os.listdir("/sys/class/drm")):
                    if not ent.startswith(card + "-"):
                        continue
                    status = _read_text(f"/sys/class/drm/{ent}/status")
                    modes = _read_text(f"/sys/class/drm/{ent}/modes").splitlines()
                    connectors.append({
                        "name": ent.split("-", 1)[1],
                        "status": status or "unknown",
                        "bestMode": modes[0] if modes else "",
                    })
                break

        cards.append({
            "slot": slot,
            "name": desc,
            "pciId": pci_id,
            "driver": drv or "（未绑定驱动）",
            "card": card_name,
            "vramBytes": vram,
            "vramUsedBytes": vram_used,
            "connectors": connectors,
        })

    # nvidia-smi（装了专有驱动才有；救援环境通常没有，输出提示即可）
    nvidia = run(["nvidia-smi"], timeout=10) or ""

    # 内核里 GPU 相关的报错/警告（限 60 行，dmesg 里噪音很多）
    dmesg = run(["dmesg", "--level=err,warn"], timeout=10) or ""
    gpu_err = [l for l in dmesg.splitlines()
               if re.search(r"drm|amdgpu|i915|nouveau|nvidia|gpu|vga", l,
                            re.IGNORECASE)][:60]

    return {
        "ok": bool(cards),
        "cards": cards,
        "nvidiaSmi": nvidia,
        "kernelErrors": gpu_err,
        "note": "没有检测到显卡（lspci 无 VGA/3D 控制器）。"
                if not cards else "",
    }


# ---------------------------------------------------------------------------
# SSH：开关 sshd + 改 root 密码（远程协助的通道）
# ---------------------------------------------------------------------------
# 远程协助的诚实形态：救援环境是内存盘、无公网入口，"远程"指的是
# 局域网内技术人员通过 SSH 连进来操作。开启 = 启动 sshd + 允许 root
# 密码登录（改 sshd_config，内存盘重启即还原）+ 设一个一次性密码。

SSH_PORT_DEFAULT = 22


def _ensure_sshd_root_login():
    """允许 root 密码登录。必须在 sshd 启动前写进配置，否则密码对了也进不来。"""
    cfg = "/etc/ssh/sshd_config"
    if not os.path.isfile(cfg):
        return
    try:
        with open(cfg, "r", encoding="utf-8") as fh:
            text = fh.read()
        if re.search(r"^PermitRootLogin\s+yes", text, re.MULTILINE):
            return
        if re.search(r"^#\s*PermitRootLogin\s+\S+", text, re.MULTILINE):
            text = re.sub(r"^#\s*PermitRootLogin\s+\S+.*$",
                          "PermitRootLogin yes", text, flags=re.MULTILINE)
        elif re.search(r"^PermitRootLogin\s+\S+", text, re.MULTILINE):
            text = re.sub(r"^PermitRootLogin\s+\S+.*$",
                          "PermitRootLogin yes", text, flags=re.MULTILINE)
        else:
            text += "\nPermitRootLogin yes\n"
        with open(cfg, "w", encoding="utf-8") as fh:
            fh.write(text)
    except OSError:
        pass


def ssh_status():
    """sshd 安装/运行状态 + 本机 IP + 端口。"""
    installed = shutil.which("sshd") is not None
    active = run(["systemctl", "is-active", "ssh"]) == "active" or \
             run(["systemctl", "is-active", "ssh.socket"]) == "active"
    port = SSH_PORT_DEFAULT
    cfg = "/etc/ssh/sshd_config"
    if os.path.isfile(cfg):
        try:
            with open(cfg, "r", encoding="utf-8") as fh:
                for line in fh:
                    m = re.match(r"^Port\s+(\d+)", line)
                    if m:
                        port = int(m.group(1))
                        break
        except OSError:
            pass
    ips = [ip for ip in (run(["hostname", "-I"]) or "").split() if ip]
    passset = " set " in (run(["passwd", "-S", "root"]) or "") + " "
    return {
        "ok": True,
        "installed": installed,
        "active": active,
        "port": port,
        "ips": ips,
        "rootPasswordSet": passset,
        "user": "root",
    }


def ssh_control(body):
    """action: start / stop / setpass。返回最新 status。"""
    action = body.get("action")
    if action not in ("start", "stop", "setpass"):
        return {"ok": False, "reason": "action 必须是 start / stop / setpass"}, 400

    if action == "setpass":
        pwd = body.get("password") or ""
        # 密码白名单：可见 ASCII、无空白，6-64 位。走 chpasswd 不进 shell。
        if not re.match(r"^[!-~]{6,64}$", pwd):
            return {"ok": False, "reason":
                    "密码需 6-64 位，只能用字母、数字和常见符号（不含空格）"}, 400
        p = subprocess.Popen(["chpasswd"], stdin=subprocess.PIPE,
                             text=True)
        p.communicate(f"root:{pwd}\n")
        if p.returncode != 0:
            return {"ok": False, "reason": "设置密码失败"}, 500
        # 设了密码顺手保证 root 能密码登录
        _ensure_sshd_root_login()
        return {"ok": True, "status": ssh_status()}, 200

    if not shutil.which("sshd"):
        return {"ok": False, "reason": "救援系统未内置 SSH 服务"}, 400

    if action == "start":
        _ensure_sshd_root_login()
        run(["systemctl", "start", "ssh"], timeout=30)
        # Debian 16+ 可能只有 socket 激活单元
        run(["systemctl", "start", "ssh.socket"], timeout=30)
        if not ssh_status()["active"]:
            return {"ok": False, "reason":
                    "sshd 启动失败（查看串口/终端里的报错）"}, 500
    else:
        run(["systemctl", "stop", "ssh"], timeout=30)
        run(["systemctl", "stop", "ssh.socket"], timeout=30)
    return {"ok": True, "status": ssh_status()}, 200


ROUTES = {
    "/api/system": system_info,
    "/api/sysinfo": sysinfo_overview,
    "/api/disks": lambda: {"disks": list_disks()},
    "/api/network": network_status,
    "/api/wifi": wifi_scan,
    "/api/tools": tool_availability,
    "/api/backups": read_backup_index,
    "/api/rescue": rescue_partition,
    "/api/jobs": list_jobs,
    "/api/sensors": sensors_data,
    "/api/shares": list_shares,
    "/api/gpu": gpu_data,
    "/api/ssh": ssh_status,
    "/api/proxy": proxy_status,
}

# 需要查询串的 GET 路由。和 ROUTES 分开是因为签名不同，混在一个表里
# 就得在调用点判断该不该传参，那种 if 迟早会传错。
QUERY_ROUTES = {
    "/api/browse": browse,
    "/api/smart": smart_data,
}


class Handler(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"

    def log_message(self, fmt, *args):
        pass  # kiosk 环境不需要访问日志刷屏

    def _send(self, code, body, ctype):
        payload = body if isinstance(body, bytes) else body.encode("utf-8")
        self.send_response(code)
        self.send_header("Content-Type", ctype)
        self.send_header("Content-Length", str(len(payload)))
        self.send_header("Cache-Control", "no-store")
        self.end_headers()
        self.wfile.write(payload)

    def _json(self, code, data):
        self._send(code, json.dumps(data, ensure_ascii=False), "application/json")

    def do_GET(self):
        parsed = urlparse(self.path)
        path = parsed.path

        if path in ROUTES or path in QUERY_ROUTES:
            try:
                if path in ROUTES:
                    data = ROUTES[path]()
                else:
                    data = QUERY_ROUTES[path](parse_qs(parsed.query))
            except Exception as exc:  # 后端崩了也要让 UI 显示错误而不是白屏
                self._json(500, {"error": str(exc)})
                return
            self._json(200, data)
            return

        # /api/jobs/<id> —— 轮询单个作业的进度
        if path.startswith("/api/jobs/"):
            job = JOBS.get(path[len("/api/jobs/"):])
            if not job:
                self._json(404, {"error": "作业不存在"})
                return
            self._json(200, job.snapshot())
            return

        # /api/ 前缀但既不在 ROUTES 也不是 /api/jobs/<id>：返回 JSON 404。
        # 不能落到下面静态文件的 text/plain 404——前端 fetch().json() 会崩。
        if path.startswith("/api/"):
            self._json(404, {"error": "no such endpoint"})
            return

        rel = "index.html" if path in ("/", "") else path.lstrip("/")
        target = os.path.normpath(os.path.join(WWW_ROOT, rel))
        # 必须比到路径分隔符，否则 /opt/kla/www-xxx 会被当成 /opt/kla/www 的子路径
        inside = target == WWW_ROOT or target.startswith(WWW_ROOT + os.sep)
        if not inside or not os.path.isfile(target):
            self._send(404, "not found", "text/plain; charset=utf-8")
            return

        ctype = MIME.get(os.path.splitext(target)[1], "application/octet-stream")
        with open(target, "rb") as fh:
            self._send(200, fh.read(), ctype)

    def do_POST(self):
        path = urlparse(self.path).path

        # 请求体大小要设上限。这个服务只绑 127.0.0.1，但一个失控的前端脚本
        # 就能把内存吃光——救援环境没有 swap，OOM 掉的是整个救援会话。
        try:
            length = int(self.headers.get("Content-Length") or 0)
        except ValueError:
            length = 0
        if length > 1024 * 1024:
            self._json(413, {"error": "请求体过大"})
            return
        raw = self.rfile.read(length) if length else b"{}"
        try:
            body = json.loads(raw.decode("utf-8") or "{}")
        except (UnicodeDecodeError, json.JSONDecodeError) as exc:
            self._json(400, {"error": f"请求体不是合法 JSON：{exc}"})
            return
        if not isinstance(body, dict):
            self._json(400, {"error": "请求体必须是对象"})
            return

        if path == "/api/jobs":
            try:
                data, code = submit_job(body)
            except Exception as exc:
                self._json(500, {"error": str(exc)})
                return
            self._json(code, data)
            return

        # /api/jobs/<id>/cancel —— 只置标志位，由作业线程自己在下一个检查点退出。
        # 不去 kill 线程：拷贝拷到一半被硬杀，目标卷上会留下半截文件。
        if path.startswith("/api/jobs/") and path.endswith("/cancel"):
            job = JOBS.get(path[len("/api/jobs/"):-len("/cancel")])
            if not job:
                self._json(404, {"error": "作业不存在"})
                return
            job.cancel.set()
            self._json(200, {"ok": True, "job": job.snapshot()})
            return

        # /api/launch —— 启动图形程序（gparted / chromium / testdisk / chntpw 等）
        if path == "/api/launch":
            try:
                data, code = launch_app(body)
            except Exception as exc:
                self._json(500, {"error": str(exc)})
                return
            self._json(code, data)
            return

        # /api/shares —— 挂载 SMB 共享
        if path == "/api/shares":
            try:
                data, code = mount_share(body)
            except Exception as exc:
                self._json(500, {"error": str(exc)})
                return
            self._json(code, data)
            return

        # /api/shares/<name>/unmount —— 卸载 SMB 共享
        if path.startswith("/api/shares/") and path.endswith("/unmount"):
            name = path[len("/api/shares/"):-len("/unmount")]
            try:
                data, code = unmount_share(name)
            except Exception as exc:
                self._json(500, {"error": str(exc)})
                return
            self._json(code, data)
            return

        # /api/wifi/connect —— 连接 Wi-Fi（密码走 POST 体，不进 URL/日志）
        if path == "/api/wifi/connect":
            try:
                data, code = wifi_connect(body)
            except Exception as exc:
                self._json(500, {"error": str(exc)})
                return
            self._json(code, data)
            return

        # /api/wifi/disconnect —— 断开 Wi-Fi
        if path == "/api/wifi/disconnect":
            try:
                data, code = wifi_disconnect(body)
            except Exception as exc:
                self._json(500, {"error": str(exc)})
                return
            self._json(code, data)
            return

        # /api/power —— 重启 / 关机
        if path == "/api/power":
            try:
                data, code = power_action(body)
            except Exception as exc:
                self._json(500, {"error": str(exc)})
                return
            self._json(code, data)
            return

        # /api/ssh —— 开关 sshd / 设置 root 密码（远程协助通道）
        if path == "/api/ssh":
            try:
                data, code = ssh_control(body)
            except Exception as exc:
                self._json(500, {"error": str(exc)})
                return
            self._json(code, data)
            return

        # /api/proxy —— 设置/清除应急网络代理（访问 GitHub 等）
        if path == "/api/proxy":
            try:
                data, code = proxy_control(body)
            except Exception as exc:
                self._json(500, {"error": str(exc)})
                return
            self._json(code, data)
            return

        self._json(404, {"error": "no such endpoint"})


def main():
    server = ThreadingHTTPServer(("127.0.0.1", PORT), Handler)
    try:
        with open(READY_FLAG, "w", encoding="utf-8") as fh:
            fh.write(str(os.getpid()))
    except OSError:
        pass
    server.serve_forever()


if __name__ == "__main__":
    main()
