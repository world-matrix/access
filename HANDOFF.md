# KARL'S LIGHT ACCESS — 交接文档

**交接日期**：2026-08-24
**工程根目录**：`D:\work\KARLS_LIGHT_ACCESS\`

复刻 IBM ThinkPad「Access IBM 预桌面区域」的现代实现：在本机硬盘开辟隐藏分区，
装入自包含的 Linux 救援系统，开机按键即可进入，不依赖任何外部介质。

先读这一份，再按下面的指引读 `kla/docs/` 里的设计文档。

---

## 0. 五分钟上手

```powershell
# 每个新开的 PowerShell 窗口都要先跑这一句（Win10 默认执行策略是 Restricted）
Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass -Force

# 看 Windows 主程序长什么样（已能编译、能跑）
D:\work\KARLS_LIGHT_ACCESS\app\bin\KarlsLightAccess.exe

# 前端静态自检（约 2 秒，不需要 Linux，不需要管理员）
D:\work\KARLS_LIGHT_ACCESS\kla\live\Test-Frontend.ps1
```

第三条应当输出「失败 0 项」。若不是，先解决它再做别的——它是整个工程里最快的
健康检查。

---

## 1. 目录结构

```
D:\work\KARLS_LIGHT_ACCESS\
├── HANDOFF.md                     ← 本文档
│
├── kla\                           救援系统（Linux 侧，阶段 0–4）
│   ├── README.md                  原始项目说明，环境准备步骤在这里
│   ├── docs\
│   │   ├── architecture.md        整体架构、磁盘布局、体积预算、启动链路
│   │   ├── backup-design.md       备份/还原设计，含 index.json 契约
│   │   └── risks.md               9 类已知风险与规避手段（R1–R9）
│   ├── live\                      Debian live-build 配置 → 产出救援 ISO
│   │   ├── build.ps1              Windows 侧构建入口（驱动 WSL）
│   │   ├── build.sh               Linux 侧实际构建脚本
│   │   ├── Setup-BuildHost.ps1    一键搭建 KLA-Build 这个 WSL 发行版
│   │   ├── Test-Frontend.ps1      前端静态自检（图标/DOM id/资源/CSS）
│   │   ├── test-access-api.sh     后端 API + 前端冒烟的端到端测试
│   │   ├── verify-iso.sh          ISO 产物校验
│   │   ├── kla-rescue-amd64.hybrid.iso        ← 构建产物，689 MiB
│   │   ├── config\includes.chroot\opt\kla\    ← ACCESS 界面源码（重点）
│   │   │   ├── server.py            后端，约 34 KB，无框架依赖
│   │   │   └── www\                 前端 index.html / app.js / app.css
│   │   └── .cache\                调试脚本与测试驱动（见 §6 说明）
│   └── scripts\                   Windows 侧运维脚本
│       ├── Prep-WslOffline.ps1      离线装 WSL2（绕开 Windows Update）
│       ├── Get-KlaFirmwareInfo.ps1  只读探测 UEFI 引导配置
│       ├── KlaFirmware.ps1          UEFI 变量读写库（引导相关的唯一入口）
│       ├── Restore-BootOrder.ps1    引导顺序一键还原（救砖用）
│       ├── Test-KlaFirmware.ps1     固件解析函数的单元测试，不需管理员
│       ├── Phase0-VmSandbox.ps1     Hyper-V 沙盘搭建 + 快照回滚
│       ├── Import-BrandAssets.ps1   品牌资产导入 ACCESS 前端
│       └── Fix-ScriptEncoding.ps1   补 UTF-8 BOM（见 §7 编码陷阱）
│
└── app\                           Windows 主程序（WPF，阶段 5）
    ├── src\                       C# 源码，11 个文件
    │   ├── App.cs / MainWindow.cs / Wizard.cs / Ui.cs / Theme.cs
    │   ├── Backup.cs / Deployment.cs / Storage.cs / Firmware.cs
    │   ├── Json.cs                  手写 JSON（不引第三方依赖）
    │   └── Branding.cs              版本号与公司名的唯一出处
    ├── build\
    │   ├── Build.ps1                编译入口，编完自动跑自检
    │   ├── Make-Assets.ps1          从品牌 LOGO 生成 ico/png 资源
    │   └── Probe-Mark.ps1           一次性的图形测量脚本（可删）
    ├── assets\                     app.ico / logo.png / mark.png
    ├── obj\                        编译中间产物（manifest、AssemblyInfo）
    └── bin\KarlsLightAccess.exe    ← 可执行文件，578 KB
```

---

## 2. 当前状态

### 已完成

| 项 | 状态 | 位置 / 证据 |
|---|---|---|
| 救援 ISO 构建打通 | ✅ | `kla/live/kla-rescue-amd64.hybrid.iso`，689 MiB，8/23 23:54 构建 |
| wimlib 元数据保真度验证 | ✅ | 18/18 + 14/14 全过（NTFS 权限、时间戳、备用数据流、中文文件名） |
| 救援分区文件系统决策 | ✅ | 选定 ext4 |
| ACCESS 前端界面 | ✅ | 4 大分类 12 个功能项，静态自检 0 失败 |
| ACCESS 后端 server.py | ✅ 已写完 | 作业队列、进度轮询、取消、磁盘浏览 API 齐备 |
| Windows 主程序骨架 + 暗黑主题 | ✅ | 编译通过，自检 3/3，`app/bin/KarlsLightAccess.exe` |
| 应用图标 | ✅ | `app/assets/app.ico`，9 个尺寸，小尺寸为重画的矢量星芒 |
| 引导安全网 | ✅ | `Restore-BootOrder.ps1` + `Test-KlaFirmware.ps1` 已实现并测过 |

### 进行中 / 未做

| 项 | 状态 | 备注 |
|---|---|---|
| **ACCESS 端到端测试** | 🔨 **写完了，一次都没跑过** | `test-access-api.sh`，见 §4 —— **接手第一件事** |
| ACCESS 功能后端接线 | 🔨 12 项中仅 1 项可用 | 见下方功能矩阵 |
| GRUB 热键 + BootNext | ⬜ 未开始 | 壁纸和美化归到这里，素材见 §5 |
| 隐藏分区创建 + 镜像部署 | ⬜ 未开始 | |
| **wimlib 交叉编译到 Windows** | ⬜ 未开始 | **卡着主程序的备份/还原功能页** |
| 安装程序、打包、签名 | ⬜ 未开始 | |

### ACCESS 功能矩阵

界面上 12 项功能，**UI 与帮助文案全部完整**，但只有一项接了真实后端。
代码里用 `ready: true` 标记真正可用的项（`www/app.js`），其余渲染为「尚未实现」。

| 分类 | 功能 | 后端 | 依赖 |
|---|---|---|---|
| 救援与恢复 | 恢复出厂 | ⬜ | wimlib |
| 救援与恢复 | 从备份还原 | ⬜ | wimlib |
| 救援与恢复 | **文件救援** | ✅ **已实现** | ntfs-3g |
| 配置 | 分区管理 | ⬜ | gparted |
| 配置 | Windows 账户 | ⬜ | |
| 配置 | 修复启动 | ⬜ | |
| 联网 | 网络设置 | ⬜ | |
| 联网 | 打开浏览器 | ⬜ | chromium |
| 联网 | 网络位置 | ⬜ | |
| 诊断 | 硬件诊断 | ⬜ | smartctl |
| 诊断 | 内存测试 | ⬜ | |
| 诊断 | 数据恢复 | ⬜ | |

> 注：早前的口头汇总说「简易浏览器没有」，实际是**入口和帮助文案都在，后端未接**
> （`app.js` 里 `id: 'browser'`）。差别在于这项不需要从零设计界面。

Windows 主程序侧同样是界面先行：备份按钮会弹「备份引擎尚未就位」
（`app/src/MainWindow.cs:365`），因为缺 wimlib for Windows。

---

## 3. 建议的推进顺序

1. **跑通 `test-access-api.sh`** —— 唯一验证 `server.py` 的手段，写完从没执行过。
   在此之前，「后端已写完」只是一个未经检验的说法。
2. **GRUB 热键 + 壁纸美化** —— 独立、见效快，不依赖任何未完成项。
3. **wimlib 交叉编译到 Windows** —— 真正的瓶颈。做完才能同时解锁
   ACCESS 的「恢复出厂 / 从备份还原」和 Windows 主程序的备份/还原功能页。
4. 隐藏分区创建与镜像部署。
5. 安装程序、打包、签名。

前任的判断（先做 GRUB 美化再补 wimlib）合理，此处保留。唯一调整是把
第 1 项提到最前：测试没跑过，后面每一步都建立在未验证的基础上。

---

## 4. 立刻要做的第一件事：跑端到端测试

```powershell
# 需要管理员（要 losetup、mount、chroot）
wsl.exe -d KLA-Build -u root -- bash /mnt/d/work/KARLS_LIGHT_ACCESS/kla/live/test-access-api.sh
```

这个脚本会：造两个真的 NTFS 回环卷 → 铺测试数据（含中文文件名和一个指向 `/etc`
的符号链接，用来验证 `safe_join` 拦不拦得住越权）→ 起 `server.py` → 跑 API 测试
→ 用无头 chromium 走一遍完整前端流程（36 条断言）。

**已知会遇到的问题**：交接时 WSL 起不来，`ext4.vhdx` 被占用报
「另一个程序正在使用此文件」。机器上 VMware 的服务在跑
（`vmware-authd` / `vmware-usbarbitrator64`），它和 WSL2 会抢虚拟化层。
先试 `wsl --shutdown`；不行就退出 VMware 或重启机器。
**这是环境问题，与代码无关。**

脚本本身的路径已改为从自身位置推导（`$LIVE`），搬到任何目录都能跑。

---

## 5. 品牌素材

**不在工程目录内**，位于 `D:\work\公司\公司\LOGO\access\`：

| 文件 | 大小 | 用途 |
|---|---|---|
| `ACCESS LOGO.png` | 64 KB | 图标与前端 logo 的源，`Make-Assets.ps1` 读它 |
| `access wallpaper.jpg` | 2.91 MB | GRUB 壁纸候选（16:9） |
| `access wallpaper 1610.jpg` | 3.65 MB | GRUB 壁纸候选（16:10） |
| `access.psd` | 38 MB | 分层源文件 |

GRUB 的 `gfxmode` 计划设为 `auto`，按实际分辨率挑一张再缩放。

⚠ 这条路径写死在 `app/build/Make-Assets.ps1` 的 `$Src` 里（已加注释标出）。
换机器时改那一行。工程内其余路径均已改为相对推导。

---

## 6. 各部分怎么跑

### ACCESS 救援系统（Linux 侧）

```powershell
# 首次：搭建 WSL 构建宿主（会造一个叫 KLA-Build 的发行版）
kla\live\Setup-BuildHost.ps1

# 构建 ISO（约 30–60 分钟，日志写到 live\run.log）
cd D:\work\KARLS_LIGHT_ACCESS\kla\live
.\build.ps1
```

构建结束会自动核对 1 GB 体积预算，超了直接报错。

### Windows 主程序

```powershell
cd D:\work\KARLS_LIGHT_ACCESS\app\build
.\Build.ps1                    # 编译 + 自检
.\Build.ps1 -SkipAssets        # 图片没改时快一点
```

编译器是 Windows 自带的 `csc.exe`（v4.0.30319，**只认 C# 5**）——
不能用字符串内插、`?.`、表达式体成员、`nameof`。整个 `src\` 都照这个约束写的。

### 只读探测（随时可跑，不改任何东西）

```powershell
kla\scripts\Get-KlaFirmwareInfo.ps1    # 需管理员，列出 UEFI 引导项
kla\scripts\Test-KlaFirmware.ps1       # 不需管理员，固件解析函数单测
kla\live\Test-Frontend.ps1             # 不需管理员，前端静态自检
```

### `.cache\` 是什么

`kla/live/.cache/` 里是调试脚本和**测试链的必需件**，不是临时垃圾：

- `api-client.py`、`smoke-driver.js` —— `test-access-api.sh` 直接依赖，删了测试跑不起来
- `kla-trixie-rootfs.tar.gz`（91 MiB）—— 构建宿主的 rootfs 缓存
- `probe-*.sh`、`dbg-*.sh` —— 排查特定问题时留下的，可作为参考

---

## 7. 这个工程的三个坑（都是踩过的）

### 7.1 WSL 有两个发行版，默认那个是空的

```
Debian      ← 默认，基本是空的
KLA-Build   ← 构建环境在这里
```

`wsl.exe` 不带 `-d` 会进 `Debian`。跑任何 KLA 相关的脚本一律写：

```powershell
wsl.exe -d KLA-Build -u root -- bash /mnt/d/work/KARLS_LIGHT_ACCESS/...
```

（`-u root`：KLA-Build 是 minbase 造的，**没有 sudo**。）

**症状极具迷惑性**：看起来像构建产物被删了，而不是走错了发行版——
报 `chroot: failed to run command '/usr/bin/env'`，`/root/kla-build` 只剩几十 KB，
`df` 显示磁盘几乎全空。之所以像「被删」而不是「不存在」，是因为脚本里的
`mkdir -p` 会在错误的发行版上把整条路径凭空造出来。
**先 `wsl.exe -l -v` 确认发行版，再怀疑数据。**

### 7.2 给 WSL 跑 shell 一律落成 .sh 文件

不要把脚本内容塞进 `wsl.exe -- bash -c "..."`。参数在 Windows→WSL 边界会被
重新解析一遍，引号、`$`、括号全会走样。失败形式不止一种：有时报语法错误，
有时更坏——**什么都没执行却返回退出码 0**，看起来像成功了。

另外 PowerShell 的转义符是反引号不是反斜杠，`\$(cmd)` 会在 Windows 侧就被
求值掉，得写 `` `$(cmd) ``。

### 7.3 带中文的 .ps1 必须存成 UTF-8 with BOM

PowerShell 5.1 读没有 BOM 的 `.ps1` 时用系统 ANSI 代码页（中文系统 = 936/GBK）。
丢了 BOM 会在**解析阶段**乱码，而乱码里某些字节会提前终止字符串字面量——
报出来的是「意外的标记」「缺少右括号」这类**指向完全无关行号**的错误。

用非 BOM 编辑器改过脚本之后，跑一次 `kla\scripts\Fix-ScriptEncoding.ps1`。

同理，`csc.exe` 编译时的 `/codepage:65001` 也是硬要求——`src\` 下的 `.cs`
是 UTF-8 无 BOM，少了这个开关每个中文字面量都会变成乱码，**且编译不报错**，
要等界面画出来才看得见。

---

## 8. 动手前务必知道的约束

摘自 `kla/README.md` 和 `kla/docs/risks.md`，展开细节看原文。

1. **无法修改 BIOS 固件**。Intel Boot Guard 阻止未签名固件写入。按键触发改由
   GRUB2 `menuentry --hotkey=` 实现，位置在 POST 之后、Windows 之前。
2. **Secure Boot 下需 MOK 注册**，该步骤要求用户重启后手动确认（UEFI 物理在场
   验证），无法自动化。
3. **隐藏分区 1 GB 固定**，备份以文件形式放 `D:\KLA\`。新建备份不预占空间，
   删除备份空间立刻回到 D:，全程 0 次分区操作。
4. **目标机器没有 WinRE**。改为连 ESP 一起捕获还原；还原 ESP 时只覆盖
   `EFI\Microsoft\`，**保留 `EFI\KLA\`**，否则恢复出厂会把 KLA 自己删掉。
5. **绝不修改 Windows 原有的 `Boot####` 项**，只新增。改 `BootOrder` 前先备份，
   出事用 `Restore-BootOrder.ps1` 还原。真到 GRUB 坏得出不来菜单时，开机按
   F12/F9/Esc 调出**固件启动菜单**手选 Windows——那是硬件级的，永远救得回来。
6. **救援环境没有声音，独显机器没有硬件加速**。为压到 1 GB 主动裁掉的，
   不是 bug（`risks.md` R7b）。存储、网卡、主力文件系统的驱动一个没动。

---

## 9. 这次搬迁改了什么

工程从两处合并到 `D:\work\KARLS_LIGHT_ACCESS\`：

| 原位置 | 现位置 |
|---|---|
| `D:\chat\CHalo\kla\` | `D:\work\KARLS_LIGHT_ACCESS\kla\` |
| `D:\KARLS_LIGHT_ACCESS\` | `D:\work\KARLS_LIGHT_ACCESS\app\` |

**原目录按要求保留**，未删除。两处内容一致（81 个文件，逐字节校验通过，
ISO 的 SHA256 与构建时生成的 `.sha256` 相符）。

⚠ **原目录已是历史副本，不要在那边继续改**。

搬迁中修掉的硬编码路径（原目录里仍是旧的）：

| 文件 | 改动 |
|---|---|
| `kla/live/test-access-api.sh` | 4 处 `/mnt/d/chat/CHalo/...` → 从脚本位置推导 `$LIVE`，并给 cp 加了失败检查 |
| `app/build/Make-Assets.ps1` | 输出目录 → 相对推导；美术源路径保留并加注释 |
| `app/build/Probe-Mark.ps1` | `mark.png` 路径 → 相对推导 |
| `kla/live/build.ps1`、`Setup-BuildHost.ps1` | 注释里的示例路径更新 |

搬迁后已验证：全部 `.ps1` 语法解析通过、BOM 完好、`Test-Frontend.ps1` 在新目录
跑出 0 失败、`test-access-api.sh` 的 5 个依赖文件都在且行尾符为 LF。
`test-access-api.sh` 因 §4 的 WSL 环境问题**未能实跑**。

另：`D:\Documents\Virtual Machines\kslaccess\` 有个几乎是空的 VMware 测试机
（vmdk 仅 0.3 MB），与 `Phase0-VmSandbox.ps1` 里的 Hyper-V `KLA-Sandbox` 不是
一回事，未纳入本工程。

---

## 10. 交接时的环境

- Windows 10 Pro 19045，中文系统（代码页 936）
- D: 可用 46.1 GB
- WSL2 两个发行版：`Debian`（空）、`KLA-Build`（构建环境，vhdx 6.4 GB，
  位于 `D:\WSL\KLA-Build\`）
- **没有装 git**——工程不在版本控制下，改之前自己留副本
- 没有装 node（`Test-Frontend.ps1` 会跳过真正的 JS 语法检查，只做括号计数，
  那个不算数）
- 装了 VMware，与 WSL2 抢虚拟化层，见 §4
