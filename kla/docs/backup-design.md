# 备份与还原模块设计

Windows 主程序与 ACCESS 救援环境**共用同一套备份数据和同一份索引**。这是整个
项目最重要的设计约束：两侧必须对备份格式和元数据达成完全一致，否则 Windows 里
做的备份在 ACCESS 里读不出来，整个方案就废了。

---

## 1. 备份内容分级

用户在 Windows 主程序里创建备份时，选择一个「范围」：

| 级别 | 名称 | 内容 | 用途 | 典型大小 |
|---|---|---|---|---|
| **L0** | 出厂母盘 | 整个 C 盘（建议先 sysprep） | 一键恢复出厂 | 12–20 GB |
| **L1** | 完整系统 | 整个 C 盘，含用户数据 | 整机灾难恢复 | 30–80 GB |
| **L2** | 系统与程序 | C 盘，排除用户 profile 数据区 | 系统坏但数据在别的盘 | 15–30 GB |
| **L3** | 用户数据 | 桌面/文档/图片/视频/下载 + 自定义目录 | 日常防误删 | 差异极大 |
| **L4** | 自定义 | 用户在目录树上勾选 + 排除规则 | 精确控制 | — |

L0 与 L1 的区别只在语义（L0 被标记为「出厂基准」，ACCESS 的「恢复出厂」按钮
只认它），底层格式完全相同。

### 默认排除规则

所有级别都无条件排除，避免捕获无意义的巨大文件：

```
\pagefile.sys
\hiberfil.sys
\swapfile.sys
\System Volume Information
\$Recycle.Bin
\Windows\Temp
\Users\*\AppData\Local\Temp
\Users\*\AppData\Local\Microsoft\Windows\INetCache
```

L2 在此基础上追加排除 `\Users\*\{Desktop,Documents,Pictures,Videos,Music,Downloads}`。

---

## 2. 存储格式：WIM + 增量链

统一使用 **WIM**（`wimlib`），理由：

- Windows 侧 `wimlib-imagex` 与 Linux 侧 `wimcapture`/`wimapply` 是**同一个库**，
  跨平台读写零风险
- 原生保留 NTFS ACL、备用数据流、硬链接、重解析点
- 单文件级去重（同一 WIM 内相同内容只存一份）
- 支持 `--delta-from` 做增量

### 增量链结构

```
chain-2026-08/
├── base.wim          全量（链的起点）
├── d001.wim          增量，parent = base
├── d002.wim          增量，parent = d001
└── d003.wim          增量，parent = d002
```

**关键限制：链是有序依赖的。** 删除 `d001` 会让 `d002`/`d003` 全部失效。UI 必须
把链可视化成一条时间线，删除中间节点时明确警告「将同时删除其后 N 个还原点」。

**保留策略**：默认每月起一条新链（新全量），旧链整条保留 3 条，超出后整条删除。
整条删除是安全的，不会破坏其他链。

### 捕获命令

```powershell
# 全量（--snapshot 让 wimlib 自动建 VSS 卷影副本）
wimlib-imagex capture C:\ base.wim "L1 2026-08-23" `
    --snapshot --compress=LZX --config=exclude.ini --check

# 增量
wimlib-imagex capture C:\ d001.wim "L1 2026-08-30" `
    --snapshot --delta-from=base.wim --compress=LZX --config=exclude.ini --check
```

> **VSS 是必须的。** 不走卷影副本，注册表 hive、正在写入的数据库等锁定文件会
> 捕获失败或产生不一致的快照，还原出来的系统可能起不来。

---

## 3. 存储位置

目标机器只有 238.5 GB，全盘自由空间 94.6 GB，装不下「隐藏分区里塞几十 GB
镜像」的原方案。改为拆分：

| 内容 | 位置 | 大小 | 理由 |
|---|---|---|---|
| 救援系统 | KLA_RESCUE 隐藏分区（盘末，ext4） | **1 GB，固定不变** | 必须独立分区才能引导 |
| 母盘 + 备份链 | `D:\KLA\` 普通目录 | **按需，用多少占多少** | 同盘不同卷，C: 被覆盖时不受影响 |
| 可选归档 | 外置盘 / SMB | — | 全量 L1 备份体积大，建议外置 |

### 为什么备份用文件而不是独立分区

这是本项目被问得最多的设计点。结论：**文件方案免费拿到了「用多少占多少、
删了就还回来」，而分区方案要用一轮分区手术才能换到同样的效果，还换不全。**

- 新建备份 = 写文件，D: 可用空间自然减少，**不预占任何空间**
- 删除备份 = 删文件，空间**立刻**回到 D:，无需任何操作
- 分区方案下「把空间还给 D:」还受**相邻性**限制：盘末新建的备份分区排在
  KLA_RESCUE 之后，删掉它腾出的空间和 D: 中间隔着 KLA_RESCUE，D: 根本扩不过去

完整论证见 architecture.md「为什么备份不用独立分区」。

### 目录保护

独立分区唯一强过文件夹的地方是不可见、难误删。用 ACL 补上：

| 手段 | 作用 |
|---|---|
| 隐藏 + 系统属性 | 资源管理器默认设置下不显示 |
| `Users` 组 **拒绝删除** ACE | 普通操作删不掉，需管理员显式改权限 |
| 主程序启动时校验完整性 | 目录缺失/被改立即告警 |

防呆不防拆——但管理员同样能在磁盘管理里删掉一个隐藏分区，两者防护级别本就相当。

### 例外：机器上只有 C: 一个卷

没有独立数据卷时备份无处可放——放 C: 会在恢复出厂时被一起覆盖。
这种机器上部署向导**会**新建一个数据分区，但：

- 只在部署时建**一次**，之后永不调整
- 建完格式化为 NTFS 并分配盘符，对用户就是一个普通数据盘
- 备份仍然以文件形式放在它的 `\KLA\` 下，删除备份照样立刻释放空间

**收缩可行性已实测**（`Get-PartitionSupportedSize`，D:）：

```
SizeMin 69.4 GB   SizeMax 118.2 GB   最多可释放 48.8 GB
```

只需要 1 GB，余量近 50 倍。D: 已用 69.3 GB 而收缩下限 69.4 GB，两者几乎重合，
说明卷尾没有不可移动文件。详见 risks.md R1。

### 隐藏分区参数

不分配盘符，用户在资源管理器里看不到；GPT 上设为 Windows Recovery 类型：

```
set id=de94bba4-06d1-4d40-a16a-bfd50179d6ac
gpt attributes=0x8000000000000001
```

Windows 侧若需访问（诊断用途），走卷 GUID 路径：

```
\\?\Volume{xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx}\
```

### 容量估算（基于实测）

C: 已用 74.3 GB。LZX 压缩后母盘体积按范围不同：

| 范围 | 源大小 | 母盘估算 |
|---|---|---|
| L1 完整系统 | 74.3 GB | 33–40 GB |
| **L2 系统与程序**（推荐做母盘） | ~50 GB | **22–28 GB** |

D: 收缩 1 GB 后剩余 47.9 GB 可用 → 放一个 L2 母盘后还剩约 22 GB 给增量链。
**建议把 L1 全量备份放外置盘**——不是因为放不下，而是同一块物理盘上的备份
挡不住硬盘本身故障。

---

## 4. index.json —— 两侧唯一的契约

`D:\KLA\index.json` 是 Windows 主程序与 ACCESS 之间的接口。**任何一侧改动格式
都必须同步另一侧。**

ACCESS 侧不认盘符，它遍历所有 NTFS 卷去找根目录下的 `KLA\` 文件夹
（`server.py` 的 `find_kla_store()`）。所以**目录名 `KLA` 是硬契约**，
两侧都不能改；而卷标和盘符可以随用户变。

```json
{
  "schemaVersion": 1,
  "machineId": "SMBIOS-UUID",
  "updatedUtc": "2026-08-23T10:00:00Z",
  "chains": [
    {
      "chainId": "c-2026-08",
      "scope": "L1",
      "scopeLabel": "完整系统",
      "createdUtc": "2026-08-23T10:00:00Z",
      "entries": [
        {
          "id": "e-0001",
          "type": "full",
          "file": "chain-2026-08/base.wim",
          "parent": null,
          "sizeBytes": 32212254720,
          "createdUtc": "2026-08-23T10:00:00Z",
          "osCaption": "Windows 10 Pro 22H2",
          "sourceVolumeGuid": "{...}",
          "isFactoryBaseline": false,
          "sha256": "..."
        }
      ]
    }
  ]
}
```

写入规则：
- **原子写**——先写 `index.json.tmp` 再 `MoveFileEx` 替换，防止断电写坏
- 每次写入前校验 `schemaVersion`，不认识的版本一律只读不改
- `isFactoryBaseline: true` 的条目即 ACCESS「恢复出厂」的目标；同一时刻至多一个

---

## 5. Windows 主程序的备份管理台

### 备份列表视图
按链分组的时间线，每个还原点显示：创建时间、范围、大小、增量/全量、
是否为出厂基准、校验状态。

### 支持的操作

| 操作 | 实现 |
|---|---|
| 立即备份 | 选范围 → `wimlib-imagex capture --snapshot` |
| 计划备份 | 注册 Windows 任务计划（每周增量 / 每月全量） |
| 校验完整性 | `wimlib-imagex verify` + 比对 index 中的 sha256 |
| 浏览还原点 | `wimlib-imagex dir` 列目录；单文件提取用 `extract` |
| 提取单个文件 | `wimlib-imagex extract <wim> <image> <path> --dest-dir=...` |
| 设为出厂基准 | 改 index 中 `isFactoryBaseline` |
| 删除还原点 | 检查链依赖 → 警告 → 删文件 + 更新 index |
| 导出到外置盘 | 整链复制 + 生成独立 index |

> 挂载 WIM 做资源管理器浏览需要 WinFsp 或 Dokan 驱动，属于额外依赖。
> **先用 `dir` + `extract` 实现浏览和取文件**，不引入内核驱动。

### 还原
Windows 运行中**无法还原系统卷**。主程序的「还原」按钮只做两件事：
1. 把目标还原点 ID 写进 `D:\KLA\pending-restore.json`
2. 设置 UEFI `BootNext` 指向 KLA，然后重启

真正的还原动作在 ACCESS 里执行——开机后读到 `pending-restore.json` 直接进入
还原确认页。这与 IBM 原版的行为完全一致。

用户数据级（L3/L4）的还原不涉及系统卷，可以在 Windows 里直接做。

---

## 6. ACCESS 侧的对应实现

同一套 index.json，命令换成 Linux 版：

```bash
wimapply /mnt/kla/D/KLA/factory.wim 1 /dev/nvme0n1p3
wimlib-imagex extract <wim> 1 "/Users/karl/Documents" --dest-dir=/mnt/usb
```

### 这条还原路径验证到哪一步了

整个 KLA 押在一句话上：**Windows 侧打的包，能在 ACCESS(Linux) 里还原成一个
能引导的 Windows**。这句话拆成两半，目前一半已证、一半未证。

**A 阶段——元数据保真度（已证，2026-08-24）**

丢了会怎样：安全描述符丢 → SYSTEM 读不了自己的文件，服务全挂，起不来；
硬链接丢 → WinSxS 里几万个硬链接变成独立副本，体积爆炸到装不下；
重解析点丢 → `C:\Documents and Settings` 这类联接失效。

两个脚本，都用 **chroot 里那份会随 ACCESS 出厂的 wimlib 1.14.4**，不是宿主上另装的：

| 脚本 | 问的问题 | 结果 |
|---|---|---|
| `live/test-wimlib-ntfs.sh` | Linux 侧 wimlib 写 NTFS 会不会丢东西 | **18/18** |
| `live/.cache/inspect-real-wims.sh` | Linux 侧 wimlib 读不读得懂 Windows 打的包 | **14/14** |

前者造一棵含硬链接/符号链接/ADS/稀疏文件/中文名/差异化权限的 NTFS 树，
capture → apply 到另一个卷 → 再 capture，然后三重比对：文件系统指纹、
硬链接分组、两份 WIM 的 `dir --detailed` 逐行 diff。**两份元数据完全一致**
（比对时刻意保留 `Attributes` 和安全描述符字段——那正是这一节要看的东西），
`ntfsfix -n` 无告警。

后者拿真数据验。两份都是 Windows 那边产出的，不是合成的：

| | 用户的 T14 系统备份 | 微软官方 install.esd |
|---|---|---|
| 来源 | 真实整卷捕获的自用系统 | Windows 10 安装介质 |
| 压缩 | LZX / 32 KB 块 | **LZMS 实心(solid)** / 128 KB 块 |
| 规模 | 122791 目录、363275 文件、54.3 GB | 29336 目录、104992 文件、15.8 GB |
| 硬链接节省 | **10.77 GB** | 6.05 GB |
| 详细元数据 | 973 万行 | 269 万行 |
| 非空安全描述符 | **486067 个**（去重 1716 种 ACL） | 134329 个（去重 365 种） |
| 非空 8.3 短名 | 62511 / 486067 | 71334 / 134329 |
| 多路径共享的 inode | 31872 个 | 24340 个 |
| 重解析点 | 84 处，4 种 tag | 2 处，2 种 tag |

「硬链接节省 10.77 GB」这一栏就是 WinSxS 效应的量化：还原时若把硬链接写成
独立副本，目标卷要多吃 10.77 GB。这是 A 阶段第一个脚本必须验硬链接的原因。

T14 那份出现的 4 种重解析 tag：`0xa0000003` 联接、`0xa000000c` 符号链接、
`0x8000001b` AppExecLink（应用商店应用的执行别名）、`0x80000023` AF_UNIX 套接字。
后两种是 Win10 较新的类型，wimlib 一样认得。

最该担心的是 `install.esd` 的 **LZMS 实心压缩**——这是最可能让第三方读取器
翻车的格式。对它的镜像 4（Pro）做了一次值层面的深挖：

```
安全描述符   135696 / 135696 个 inode 非空，空 0 个（去重后 381 种 ACL）
             381 种**全部通过结构校验**：Revision=1、SE_SELF_RELATIVE 置位、
             owner/group/SACL/DACL 四个偏移均在对象长度之内、全部带 DACL
solid 解析   135696 个流里有 106180 个走 Solid resource 引用
```

最后一行是关键：**四分之三以上的流是从 LZMS 实心资源里解出来的**，说明
wimlib 不是只认了个文件头，是真在解 solid 压缩。这一项本来是排在最前面的
兼容性风险，现在可以划掉。

> ⚠ 计数踩过一个坑，记下来免得重犯：`grep -c 'Security Descriptor'` 数的是
> **字段出现的行数**，而 `--detailed` 对每个 inode 都打印全套字段，所以这么数
> 出来的值永远等于 inode 总数。第一版三项数字一模一样（486067），看着像三项
> 都满分。改成按**值**统计之后，四个数字错了三个：重解析点 252 → **84**
> （`grep -ci reparse` 把 `FILE_ATTRIBUTE_REPARSE_POINT is set` 也数进去了，
> 一个点数了三遍）、硬链接 486067 → **31872**（`Link Group ID = 0` 是"不属于
> 任何组"的哨兵，得排掉）、短名 486067 → **62511**（大量是空字符串）。
> 只有安全描述符那个 486067 碰巧是对的——因为它确实每个 inode 都非空。
> 表里的数字全部是修正后的；安全描述符还额外做了结构解析，不只是判非空。

**B 阶段——还原出来的真能开机（未证）**

A 阶段证明的是「wimlib 认识的东西一样没丢」，不等于「固件和 Windows 认这个盘」。
剩下没验的是：分区还原后 BCD 的分区 GUID 引用是否真的仍然命中、ESP 的
`EFI\Microsoft\` 覆盖式还原是否完整、Windows 首次启动会不会因为 ACL 或
`\Windows\System32\config` 的状态触发 chkdsk 或蓝屏。

这一步必须在虚拟机或真机上跑，工具是 `scripts/Phase0-VmSandbox.ps1`
（Hyper-V Gen2 + Standard 检查点）。**前置条件目前不满足**：需要管理员权限、
需要 Hyper-V 已启用（当前状态未知，查询本身就要提权）、还需要装一个真 Windows
到来宾里。磁盘也紧：C: 剩 39.7 GB、D: 剩 46.6 GB，而 T14 那份 WIM 展开是 54.3 GB。

在 B 阶段过掉之前，**部署向导不得把"恢复出厂"标成已验证功能**。

### 引导恢复：连 ESP 一起捕获还原

原方案是「还原后引导进 WinRE 跑 `bcdboot`」。**目标机器实测发现根本没有
WinRE 分区，且 `reagentc /info` 显示 WinRE 状态为 Disabled**，没有委托对象，
该方案作废。

替代方案更简单也更可靠：**把 ESP 和 C: 一起捕获、一起还原**。

```bash
# 捕获（Windows 侧，部署时做一次）
wimlib-imagex capture C:\ factory.wim "L0 出厂母盘" --snapshot --compress=LZX
# ESP 只有 300 MB，直接整盘打包
mountvol S: /S && tar -cf esp-factory.tar -C S:\ .
```

```bash
# 还原（ACCESS 侧）
wimapply factory.wim 1 /dev/nvme0n1p3          # C:
mount /dev/nvme0n1p1 /mnt/esp                  # ESP
rm -rf /mnt/esp/EFI/Microsoft                  # 只重置 Windows 的部分
tar -xf esp-factory.tar -C /mnt/esp
```

**为什么这样就够了**：BCD 里的设备引用在 GPT 上是分区 GUID。还原目标是
同一批物理分区，分区 GUID 没有改变，所以捕获时那份 BCD 里的引用**还原后依然
有效**，不需要重新生成。

注意还原 ESP 时只覆盖 `EFI\Microsoft\`，**保留 `EFI\KLA\`**——否则恢复出厂
会把 KLA 自己的引导删掉，下次就进不来了。

### 这也解决了「恢复出厂后 KLA 还在吗」的问题

隐藏分区不在还原范围内，`EFI\KLA\` 被显式保留，`D:\KLA\` 也不受影响。
所以恢复出厂之后 KLA 完好无损，可以反复使用——和 IBM 原版的行为一致。
