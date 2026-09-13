<#
.SYNOPSIS
    准备 KLA 的 WSL 构建宿主：debootstrap 一个干净的 Debian trixie 并导入。

.DESCRIPTION
    微软商店的 Debian 包带的根文件系统是 2021 年的 bullseye（Debian 11），
    而 KLA 的救援盘目标是 trixie（Debian 13）。bullseye 的 debootstrap 不认识
    trixie，backports 也已随 LTS 到期下架，直接拿它构建会踩一串版本坑。

    本脚本用现有的 bullseye 做一次性跳板，bootstrap 出一个纯净的 trixie
    根文件系统，导入成独立的 WSL 发行版 KLA-Build，之后所有构建都在它里面跑。
    原来的 Debian 发行版不受影响，也不会被卸载。

    全程使用国内镜像（默认清华 TUNA）。这不是可选项——deb.debian.org 在这里
    慢到 8 MB 的索引要跑几分钟，而 lb build 要往 chroot 里拉几百 MB 的包。

.PARAMETER SourceDistro
    做跳板的现有发行版名。留空自动挑第一个 Debian。

.PARAMETER TargetDistro
    要创建的构建宿主名。默认 KLA-Build。

.PARAMETER InstallPath
    构建宿主的虚拟磁盘存放目录。构建过程会占用 10–20 GB，选个空间足的盘。

.PARAMETER Mirror
    Debian 镜像地址。默认清华 TUNA。

.PARAMETER Force
    目标发行版已存在时先注销它（会丢掉里面的全部数据）。

.EXAMPLE
    .\Setup-BuildHost.ps1
    .\Setup-BuildHost.ps1 -Mirror http://mirrors.aliyun.com/debian -InstallPath E:\WSL\KLA-Build
#>
[CmdletBinding()]
param(
    [string] $SourceDistro,
    [string] $TargetDistro = 'KLA-Build',
    [string] $InstallPath  = 'D:\WSL\KLA-Build',
    [string] $Mirror       = 'http://mirrors.tuna.tsinghua.edu.cn/debian',
    [switch] $Force
)

$ErrorActionPreference = 'Stop'

function Write-Step($m) { Write-Host ""; Write-Host "==> $m" -ForegroundColor Cyan }
function Write-Ok($m)   { Write-Host "    $m" -ForegroundColor Green }
function Write-Warn($m) { Write-Host "!!  $m" -ForegroundColor Yellow }

# wsl -l -q 的输出是 UTF-16LE，PowerShell 5.1 默认按 ANSI 读会变成乱码
function Get-WslDistros {
    $prev = [Console]::OutputEncoding
    [Console]::OutputEncoding = [System.Text.Encoding]::Unicode
    try {
        return (wsl.exe -l -q) | ForEach-Object { $_.Trim() } | Where-Object { $_ }
    } finally {
        [Console]::OutputEncoding = $prev
    }
}

# D:\work\KARLS_LIGHT_ACCESS\kla\live  ->  /mnt/d/work/KARLS_LIGHT_ACCESS/kla/live
function ConvertTo-WslPath($winPath) {
    $full = [System.IO.Path]::GetFullPath($winPath)
    $drive = $full.Substring(0, 1).ToLower()
    return "/mnt/$drive" + $full.Substring(2).Replace('\', '/')
}


# --- 1. 找跳板发行版 ---------------------------------------------------------
Write-Step '检查 WSL'

if (-not (Get-Command wsl.exe -ErrorAction SilentlyContinue)) {
    throw "未找到 wsl.exe。先跑 scripts\Prep-WslOffline.ps1。"
}

$distros = Get-WslDistros
if (-not $distros) { throw "WSL 里没有任何发行版。先跑 scripts\Prep-WslOffline.ps1 -Install。" }

if (-not $SourceDistro) {
    $SourceDistro = $distros | Where-Object { $_ -ne $TargetDistro -and $_ -match 'Debian|Ubuntu' } |
                    Select-Object -First 1
    if (-not $SourceDistro) { throw "没找到可用作跳板的 Debian/Ubuntu。已安装: $($distros -join ', ')" }
}
Write-Ok "跳板发行版: $SourceDistro"

# --- 2. 目标发行版是否已存在 -------------------------------------------------
if ($distros -contains $TargetDistro) {
    if (-not $Force) {
        Write-Warn "$TargetDistro 已存在。加 -Force 可注销重建（会丢掉里面的全部数据）。"
        Write-Host "    若只是想重新构建 ISO，直接跑：.\build.ps1 -Distro $TargetDistro" -ForegroundColor DarkGray
        return
    }
    Write-Step "注销已存在的 $TargetDistro"
    wsl.exe --unregister $TargetDistro
    if ($LASTEXITCODE -ne 0) { throw "注销 $TargetDistro 失败" }
}

# --- 3. 在跳板里 bootstrap ---------------------------------------------------
$cacheDir = Join-Path $PSScriptRoot '.cache'
New-Item -ItemType Directory -Force -Path $cacheDir | Out-Null
$tarWin = Join-Path $cacheDir 'kla-trixie-rootfs.tar.gz'
$tarWsl = ConvertTo-WslPath $tarWin
$shWsl  = ConvertTo-WslPath (Join-Path $PSScriptRoot 'setup-host.sh')

if (Test-Path $tarWin) {
    Write-Step "复用已有的 rootfs 包"
    Write-Ok "$tarWin ($([math]::Round((Get-Item $tarWin).Length / 1MB, 1)) MB)"
    Write-Host "    要重新 bootstrap 就先删掉它。" -ForegroundColor DarkGray
} else {
    Write-Step "在 $SourceDistro 里 bootstrap Debian trixie"
    Write-Host "    镜像: $Mirror" -ForegroundColor DarkGray

    # Windows 上编辑的 .sh 可能带 CRLF，bash 会报 'bad interpreter'。
    # 先剥掉再跑，同时避开 DrvFs 上执行位无效的问题。
    $inner = @"
set -e
sed 's/\r`$//' '$shWsl' > /tmp/kla-setup-host.sh
export KLA_MIRROR='$Mirror'
export KLA_OUT_TAR='$tarWsl'
bash /tmp/kla-setup-host.sh
"@
    wsl.exe -d $SourceDistro -u root -- bash -lc $inner
    if ($LASTEXITCODE -ne 0) { throw "bootstrap 失败（退出码 $LASTEXITCODE）" }
}

if (-not (Test-Path $tarWin)) { throw "预期的 rootfs 包不存在: $tarWin" }

# --- 4. 导入 -----------------------------------------------------------------
Write-Step "导入为 $TargetDistro"
New-Item -ItemType Directory -Force -Path $InstallPath | Out-Null
wsl.exe --import $TargetDistro $InstallPath $tarWin --version 2
if ($LASTEXITCODE -ne 0) { throw "导入失败（退出码 $LASTEXITCODE）" }
Write-Ok "已导入到 $InstallPath"

# --- 5. 装构建依赖 -----------------------------------------------------------
Write-Step "在 $TargetDistro 里安装构建依赖"

# live-build 拉 chroot 时要 debootstrap；xorriso 造 ISO；squashfs-tools 压
# 文件系统；ca-certificates 让 https 源可用；rsync 供 build.ps1 增量同步。
$deps = @'
set -e
export DEBIAN_FRONTEND=noninteractive
apt-get update
apt-get install -y --no-install-recommends \
    live-build debootstrap xorriso squashfs-tools \
    ca-certificates curl rsync xz-utils dosfstools mtools \
    grub-efi-amd64-bin grub-pc-bin
echo
echo "--- 版本核对 ---"
lb --version 2>/dev/null | head -1 || echo "lb: 未安装"
debootstrap --version
echo "debootstrap 认识 trixie: $(test -e /usr/share/debootstrap/scripts/trixie && echo 是 || echo 否)"
xorriso --version 2>&1 | grep -m1 xorriso
'@
wsl.exe -d $TargetDistro -u root -- bash -lc $deps
if ($LASTEXITCODE -ne 0) { throw "安装构建依赖失败（退出码 $LASTEXITCODE）" }

Write-Step '完成'
Write-Ok "构建宿主 $TargetDistro 就绪"
Write-Host ""
Write-Host "    下一步：" -ForegroundColor Green
Write-Host "      .\build.ps1 -Distro $TargetDistro" -ForegroundColor Green
