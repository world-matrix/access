# 整体架构

## 目标机器实测（2026-08-23）

```
磁盘 0   SKHynix HFS256GEJ9X164N   NVMe   238.5 GB   GPT
  1  System    (ESP)      0.3 GB
  2  Reserved  (MSR)      16 MB
  3  C:  「系统」        120.0 GB   剩余 45.7 GB   已用 74.3 GB
  4  D:  「work」        118.2 GB   剩余 48.9 GB   已用 69.3 GB
固件 UEFI

D: 收缩探测（Get-PartitionSupportedSize）
  SizeMin 69.4 GB   SizeMax 118.2 GB   最多可释放 48.8 GB
```

> D: 已用 69.3 GB，收缩下限 69.4 GB——**两者几乎重合**，说明卷尾没有不可移动
> 文件、也没有严重碎片，收缩下限就是数据本身的边界。而 KLA_RESCUE 只要 1 GB，
> 余量近 50 倍，R1（收缩风险）因此从「最危险的步骤」降级为常规操作。

**两个关键事实，直接决定了下面的设计：**

1. **没有 WinRE 分区，且 WinRE 状态为 Disabled**（`reagentc /info` 确认）。
   原先「还原后委托 WinRE 跑 bcdboot 重建引导」的方案没有委托对象，已废弃。
   改为连 ESP 一起捕获还原，见 backup-design.md §6。
2. **全盘自由空间仅 94.6 GB**。原计划的 60 GB 隐藏分区会吃掉近 2/3，不可行。
   改为「1 GB 固定小分区 + 备份以文件形式放数据卷」的拆分方案。

## 磁盘布局

部署完成后（★ 为新增）：

```
┌─ ESP (FAT32, 300 MB, 已有) ───────────────────────┐
│  EFI\Microsoft\Boot\bootmgfw.efi   Windows 引导    │
│  EFI\KLA\shimx64.efi               Secure Boot ★   │
│  EFI\KLA\grubx64.efi               KLA 引导     ★  │
│  EFI\KLA\grub.cfg                               ★  │
├─ MSR (16 MB, 已有) ────────────────────────────────┤
├─ C: 「系统」120 GB (不动) ─────────────────────────┤
├─ D: 「work」117.2 GB (从 118.2 收缩 1 GB) ─────────┤
│  └─ D:\KLA\                                     ★  │
│      ├─ index.json              两侧共用契约       │
│      ├─ factory.wim             出厂母盘 ~25 GB    │
│      ├─ esp-factory.tar         ESP 快照 ~300 MB   │
│      ├─ chain-YYYY-MM/*.wim     增量备份链         │
│      └─ pending-restore.json    Windows→ACCESS     │
├─ KLA_RESCUE (新建, 隐藏, 1 GB, 盘末) ★ ───────────┤
│  ├─ boot/vmlinuz + initrd.img      ~50 MB          │
│  └─ live/filesystem.squashfs       ~450 MB         │
└────────────────────────────────────────────────────┘
```

**关键性质：这 1 GB 是固定的，部署后再也不动。** 备份占多少空间由
`D:\KLA\` 里的文件决定，删掉备份文件空间立刻回到 D:，不需要任何分区操作。
理由见下面「为什么备份不用独立分区」。

## 体积预算

对标 IBM 的 Predesktop Area（T43 上约 700 MB–1 GB），KLA 的目标是**上盘
≤ 1 GB**。这里的「上盘」指 KLA_RESCUE 分区实际要装的东西，不含 ISO 里
那些只在光盘/U 盘启动时用到的部分（isolinux、EFI 引导镜像等）。

| 组件 | 未压缩 | squashfs (xz) | 说明 |
|---|---|---|---|
| Debian minbase | ~250 MB | ~80 MB | 系统底座 |
| Chromium（去语言包） | ~320 MB | ~125 MB | UI 引擎兼用户浏览器 |
| 内核模块（裁剪后） | ~55 MB | ~22 MB | 删了声卡/电视卡/独显/IB |
| 网卡固件 | ~90 MB | ~55 MB | 全留，用户明确要求 |
| Xorg + openbox | ~90 MB | ~30 MB | 只用 modesetting |
| 救援工具集 | ~150 MB | ~50 MB | wimlib/gparted/testdisk… |
| Python3 | ~45 MB | ~13 MB | UI 后端 |
| 字体（微米黑+DejaVu） | ~10 MB | ~9 MB | 字体本身已压缩 |
| **squashfs 合计** | | **~440 MB** | |
| vmlinuz + initrd | | ~50 MB | 不进 squashfs |
| **上盘合计** | | **~490 MB** | **占预算 48%** |

`build.sh` 在构建结束时会**实测并打印这张账**，超预算直接报错。预算不是
拍脑袋的口号，是每次构建都核对的硬约束。

### 为达成 1 GB 做的四处取舍

| 砍掉的东西 | 收益 | 代价 |
|---|---|---|
| `fonts-noto-cjk` → `fonts-wqy-microhei` | -245 MB | 字形观感略降；覆盖 GB18030 够用 |
| GPU 固件（`firmware-*-graphics`） | -200 MB | 独显机器回落 EFI framebuffer，无 3D |
| 内核模块（声卡/电视卡/独显/InfiniBand） | -90 MB | 救援环境无声音 |
| Chromium 语言包（只留 en-US/zh-CN） | -35 MB | 无 |

存储、网卡、文件系统相关的模块**一个没动**——删错这些的后果是进了救援
环境看不到硬盘或上不了网，属于致命故障，不值得为几十 MB 冒险。

> `--firmware-chroot` 必须为 `false`。它为 `true` 时 live-build 会自动把
> 归档区里所有 `firmware-*` 包塞进 chroot，上面第二行的 200 MB 会原样加回来。

## 为什么备份不用独立分区

一个自然的想法是：需要备份时新建一个备份分区，删除备份时把分区还给 D:。
**这个方案在分区表层面走不通，而且现有的文件方案已经做到了它想要的效果。**

### 分区回收受「相邻性」限制

扩展一个分区，只能吃掉**紧挨在它后面**的空闲空间。部署后的布局是：

```
… | D: | KLA_RESCUE |
```

在盘末新建备份分区只能排在 KLA_RESCUE 之后：

```
… | D: | KLA_RESCUE | BACKUP |
```

此时删掉 BACKUP，空出来的空间和 D: **中间隔着 KLA_RESCUE**，D: 扩不过去。
要还给 D:，必须先把 KLA_RESCUE 整体搬到盘末——每次都要重写几百 MB、
重算分区 GUID、更新 GRUB 配置和 UEFI 引导项。一次操作失败就进不了系统。

调换顺序（`… | D: | BACKUP | KLA_RESCUE |`）确实能让 D: 扩回去，但每次
增删备份仍然是一轮「收缩 D: → 建分区」或「删分区 → 扩展 D:」的分区手术：
慢（要搬数据）、有损坏风险（risks.md R1）、而且备份想扩容一点就得重来一遍。

### 文件方案免费拿到同样的效果

备份是 `D:\KLA\` 下的普通文件，所以：

- 新建备份 = 写文件，D: 可用空间自然减少，**不预占任何空间**
- 删除备份 = 删文件，空间**立刻**回到 D:，用户马上能用
- 备份要变大变小 = 什么都不用做

这正是「用多少占多少、删了就还回来」，而且是 0 次分区操作、0 风险、瞬时生效。
分区方案是这件事的一个更慢更危险的实现方式。

### 分区唯一的优势是防删，用 ACL 补

独立分区在资源管理器里完全不可见，文件夹会被用户误删。补法：

1. `D:\KLA\` 加 **隐藏 + 系统**属性（默认设置下资源管理器不显示）
2. 给 `Users` 组加一条**拒绝删除**的 ACE，普通操作删不掉，需要管理员显式改权限
3. 主程序启动时校验完整性，发现缺失立即告警

防呆不防拆——但独立分区同样挡不住管理员在磁盘管理里删掉它，防护级别本就相当。

### 例外：机器上只有 C: 一个卷

没有独立数据卷时，备份无处可放（放 C: 会在恢复出厂时被一起覆盖）。
这种机器上部署向导**会**新建一个数据分区，但只建**一次**，之后仍按文件管理。
详见 backup-design.md §3。

### 为什么把镜像放 D: 而不是隐藏分区

D: 和 C: 在**同一块物理盘**上。C: 被重装、格式化或覆盖时，D: 照样完好。
保护级别几乎等同隐藏分区，但不用为几十 GB 的镜像单独划分区。

代价是用户可能手动删掉 `D:\KLA\`。主程序会给该目录加隐藏+系统属性并在
启动时校验完整性，但这属于防呆不防拆——可接受。

### 为什么收缩 D: 而不是 C:

- D: 是纯数据卷，没有页面文件、休眠文件、MFT 保留区和系统文件，
  收缩失败与损坏的概率远低于 C:（见 risks.md R1）
- D: 是最后一个分区，从尾部切走的空间直接追加为盘末新分区，
  不产生分区空洞，分区编号保持连续
- C: 只剩 45.7 GB，不该再动

KLA_RESCUE 分区参数：
- 文件系统 **ext4**
- GPT 类型 GUID `de94bba4-06d1-4d40-a16a-bfd50179d6ac`（Windows Recovery）
- 属性 `0x8000000000000001`（`NO_DRIVE_LETTER` + `REQUIRED_PARTITION`）

> **ext4 vs NTFS 已决议：ext4。**
> 拆分方案落地后，这个分区只装 Linux 救援系统，Windows 侧完全不需要写它；
> 而备份和母盘落在 D: 上，那本来就是 NTFS，两侧都原生。
> 原先的纠结（谁原生读写备份）随着拆分自动消失了。

## 启动链路

```
上电
  ↓
UEFI 固件 POST
  ↓
Boot0000 = KLA GRUB (shim → grubx64.efi)
  ↓
┌───────────────────────────────────────┐
│  GRUB 菜单，3 秒倒计时，显示 KLA Logo │
│                                       │
│  [默认] Windows                       │
│  [F4]   KARL'S LIGHT ACCESS           │
│  [F5]   内存诊断 (MemTest86+)          │
└───────────────────────────────────────┘
  ↓ 超时 / 未按键              ↓ 按下 F4
chainload bootmgfw.efi      linux + squashfs
  ↓                            ↓
Windows                      ACCESS
```

### 三种进入 ACCESS 的方式

1. **开机按键** — GRUB `menuentry --hotkey=<key>`，用户在部署时自选按键
2. **Windows 侧软触发** — 写 UEFI `BootNext` 变量指向 KLA 的 `Boot####`，
   然后重启。用于「还原」按钮和桌面快捷方式
3. **故障自动进入** — GRUB 记录启动计数，Windows 连续 3 次未成功进桌面
   则默认项自动切到 ACCESS（`grub-editenv` 实现）

第 2 种通过 `SetFirmwareEnvironmentVariableW` 写 `BootNext`（全局变量 GUID
`8be4df61-93ca-11d2-aa0d-00e098032b8c`），调用方需持有
`SE_SYSTEM_ENVIRONMENT_NAME` 特权，且仅 UEFI 系统有效。

## 组件与职责

| 组件 | 语言/技术 | 职责 |
|---|---|---|
| **Kla.App** | C# / WPF | 部署向导、备份管理台、软触发 |
| **Kla.Core** | C# 类库 | 分区操作、UEFI 变量、wimlib 封装、index.json 读写 |
| **ACCESS 系统** | Debian 13 live | 救援运行时 |
| **ACCESS UI** | Python + Chromium kiosk | 救援环境内的图形菜单 |
| **GRUB 配置** | grub.cfg | 启动菜单与热键 |

### ACCESS UI 为什么用 Chromium kiosk

Chromium 本来就要装（用户需求里的「精简浏览器」），复用它做 UI 意味着：
- 零额外依赖，不引入 GTK/Qt 工具链
- HTML/CSS 做界面，视觉效果和迭代速度远好于原生工具包
- 后端用 Python 标准库 `http.server` 即可，无需框架

启动方式：`chromium --kiosk --app=http://127.0.0.1:8760/`

## ACCESS 功能清单

| 功能 | 实现 |
|---|---|
| 一键恢复出厂 | `wimapply` index 中 `isFactoryBaseline` 的条目 |
| 从备份还原 | 时间线选点 → `wimapply` 全量 + 逐级增量 |
| 文件救援 | ntfs-3g 挂载 + PCManFM + U盘/SMB/USB 导出 |
| 硬件诊断 | `smartctl`、`memtester`、`stress-ng`、`nvme-cli`、`lm-sensors` |
| 内存测试 | MemTest86+（独立 GRUB 项，不在 Linux 内） |
| 分区管理 | GParted |
| 数据恢复 | TestDisk、PhotoRec、ddrescue |
| 密码/注册表 | chntpw |
| 联网与浏览 | NetworkManager + Chromium |
| 引导修复 | 还原 ESP 中的 `EFI\Microsoft\`，见 backup-design.md §6 |
