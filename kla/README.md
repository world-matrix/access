# KARL'S LIGHT ACCESS (KLA)

复刻 IBM ThinkPad「Access IBM 预桌面区域」的现代实现。

在本机硬盘上开辟一个隐藏分区，装入一套自包含的 Linux 救援系统。开机时按下用户
自选的按键即可进入，提供恢复出厂、文件救援、备份还原、硬件诊断和联网能力——
全程不依赖任何外部介质。

Windows 侧配套一个主程序，负责部署、日常备份和备份管理。

> **恢复出厂后 KLA 依然可用。** 隐藏分区不在还原范围内，`EFI\KLA\` 在还原 ESP
> 时被显式保留，`D:\KLA\` 也不受影响——与 IBM 原版行为一致。

---

## 项目状态

| 阶段 | 内容 | 状态 |
|---|---|---|
| 0 | 虚拟机沙盘 + 回滚脚本 | 🔨 进行中 |
| 1 | Debian live-build 配置，产出可启动 ISO | 🔨 进行中 |
| 2 | ACCESS 内部 GUI（恢复/救援/备份/诊断） | ⬜ 未开始 |
| 3 | GRUB 安装 + hotkey 绑定 + BootNext 软触发 | ⬜ 未开始 |
| 4 | 隐藏分区创建 + 镜像部署 | ⬜ 未开始 |
| 5 | WPF 主程序（部署向导 + 备份管理台） | ⬜ 未开始 |

## 目录结构

```
kla/
├── docs/
│   ├── architecture.md      整体架构与启动链路
│   ├── backup-design.md     备份/还原模块设计（含 index.json 契约）
│   └── risks.md             已知风险与规避手段
├── live/                    阶段 1：Debian live-build 配置
│   ├── auto/config          lb config 参数
│   ├── config/              包列表、chroot 注入文件、构建钩子
│   ├── build.sh             Linux/WSL 侧构建脚本
│   └── build.ps1            Windows 侧包装器（驱动 WSL 构建）
├── scripts/
│   ├── Fix-ScriptEncoding.ps1  把本仓库 .ps1 重存为 UTF-8 with BOM（见下）
│   ├── Prep-WslOffline.ps1     离线准备 WSL2+Debian（绕开 WU 通道）
│   ├── Get-KlaFirmwareInfo.ps1 只读探测 UEFI 引导配置，验证软触发可行性
│   └── Phase0-VmSandbox.ps1    Hyper-V 测试环境搭建 + 快照回滚
└── src/
    └── Kla.App/             阶段 5：WPF 主程序
```

## 关键约束（务必先读）

1. **无法修改 BIOS 固件**。Intel Boot Guard 阻止未签名固件写入；UEFI `Key####`
   热键变量厂商实现率极低。按键触发改由 GRUB2 `menuentry --hotkey=` 实现，
   位置在 POST 之后、Windows 之前。
2. **Secure Boot 下需 MOK 注册**，该步骤要求用户在重启后手动确认（物理在场
   验证），无法自动化。
3. **隐藏分区 1 GB 固定，备份以文件形式放数据卷**。对标 IBM Predesktop Area
   的体积（T43 上约 700 MB–1 GB）。隐藏分区只装救援系统，部署后永不调整；
   出厂母盘和备份链是 `D:\KLA\` 下的普通文件——**新建备份不预占空间，
   删除备份空间立刻回到 D:**，全程 0 次分区操作。
   为什么不用独立备份分区（相邻性限制），见 [`docs/architecture.md`](docs/architecture.md)。
4. **目标机器没有 WinRE**（状态 Disabled，磁盘上也无恢复分区），原先「还原后
   委托 WinRE 重建引导」的方案作废。改为连 ESP 一起捕获还原——GPT 分区 GUID
   不变，捕获时的 BCD 引用还原后依然有效。
5. **压缩现有分区可能失败**——页面文件 / 休眠文件 / VSS / MFT 会阻挡收缩。
   本机 D: 实测最多可释放 48.8 GB（只需 1 GB，余量近 50 倍），但换机器仍需
   `Get-PartitionSupportedSize` 逐台探测并在失败时安全回滚。

详见 [`docs/risks.md`](docs/risks.md)。

## 快速开始（阶段 1）

需要 WSL2 + Debian，或任意 Debian 13 (trixie) 环境。

> **先解除脚本执行限制。** Windows 10 客户端默认执行策略是 `Restricted`，
> 直接跑 `.ps1` 会报 `UnauthorizedAccess`。在每个要跑脚本的窗口里执行一次：
>
> ```powershell
> Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass -Force
> ```
>
> `-Scope Process` 只影响当前进程，不写注册表、不需要管理员、关窗即恢复。
> 不要用 `-Scope CurrentUser`——那是持久化改动，为这点事留长期状态不划算。

> **中文脚本必须存成 UTF-8 with BOM。** Windows PowerShell 5.1 在读没有 BOM 的
> `.ps1` 时，用的是系统 ANSI 代码页（中文系统 = 936/GBK）而不是 UTF-8。本仓库
> 的脚本都带中文，一旦丢了 BOM 就会在**解析阶段**乱码，而且乱码里某些字节会
> 提前终止字符串字面量——报出来的是「意外的标记」「哈希文本不完整」「缺少右括号」
> 这类**指向完全无关行号**的错误，极易误判成语法问题。
>
> 新增或用非 BOM 编辑器改过脚本之后，跑一次：
>
> ```powershell
> .\scripts\Fix-ScriptEncoding.ps1
> ```
>
> 它只补 BOM，不动内容；对纯 ASCII 文件和已有 BOM 的文件跳过；遇到本来就不是
> 合法 UTF-8 的文件会保留原样并单独列出（盲目转换会把它彻底毁掉）。
> `Fix-ScriptEncoding.ps1` 自身**刻意保持纯 ASCII**，这样它在两种代码页下解析
> 结果一致——别的脚本全废时它必须还能跑。

```powershell
# 1. 下载安装包（现在就能跑，不需要重启，不需要管理员）
.\scripts\Prep-WslOffline.ps1 -Download

# 2. 重启

# 3. 离线安装（管理员）
.\scripts\Prep-WslOffline.ps1 -Install
```

> **为什么不直接 `wsl --install -d Debian`？** 它走 Windows Update 通道拉发行版，
> 国内经常返回 HTTP 503，报错 `0x80244022`。上面的脚本改从微软 CDN 直接下载，
> 两套基础设施互不影响。若你的网络没这个问题，`wsl --install -d Debian --web-download`
> 也能达到同样效果。
>
> 另外，`虚拟机平台` 和 `适用于 Linux 的 Windows 子系统` 两个组件装完**必须重启**，
> 否则 WSL2 起不来，`wsl --status` 会静默无输出。

```powershell
# 4. 构建 ISO
cd kla\live
.\build.ps1
```

产物：`live/kla-rescue-amd64.hybrid.iso`，可直接写 U 盘或挂到虚拟机验证。
构建结束会自动核对 1 GB 体积预算，超了直接报错。

### 不依赖 Linux 的验证

```powershell
# 只读探测 UEFI 引导配置（管理员），随时可跑
.\scripts\Get-KlaFirmwareInfo.ps1
```

验证「软蓝键」（写 `BootNext` 后重启进 ACCESS）在本机是否成立，
并列出现有引导项——KLA 只新增、绝不改动它们（risks.md R2）。
