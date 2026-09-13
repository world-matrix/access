<#
.SYNOPSIS
    离线准备 WSL2 + Debian，绕开 Windows Update 通道。

.DESCRIPTION
    背景：`wsl --install -d Debian` 默认走 Windows Update 拉发行版，
    国内经常返回 HTTP 503，报错 0x80244022。本脚本改为直接从微软 CDN
    下载安装包，落地到本机后再离线安装。

    分两步，中间隔一次重启：

      .\Prep-WslOffline.ps1 -Download    # 现在就能跑，不需要重启
      （重启）
      .\Prep-WslOffline.ps1 -Install     # 重启后跑，全程离线

    -Download 只是拉文件，不改任何系统状态，可以随时中断和重试。

.PARAMETER Download
    下载所需安装包到 -Path。不需要管理员权限，不需要重启。

.PARAMETER Install
    从 -Path 离线安装。需要管理员权限，且必须在启用「虚拟机平台」+
    「适用于 Linux 的 Windows 子系统」并重启之后运行。

.PARAMETER Path
    安装包存放目录。默认 %USERPROFILE%\Downloads\KLA-prep

.EXAMPLE
    .\Prep-WslOffline.ps1 -Download
    .\Prep-WslOffline.ps1 -Install
#>
[CmdletBinding(DefaultParameterSetName = 'Download')]
param(
    [Parameter(ParameterSetName = 'Download')]
    [switch]$Download,

    [Parameter(ParameterSetName = 'Install')]
    [switch]$Install,

    [string]$Path = (Join-Path $env:USERPROFILE 'Downloads\KLA-prep')
)

$ErrorActionPreference = 'Stop'
# 进度条会让 Invoke-WebRequest 慢一个数量级（每个 chunk 都重绘控制台）
$ProgressPreference = 'SilentlyContinue'

# ---------------------------------------------------------------------
# 下载清单
#
# wsl_update_x64.msi 是 Windows 10 上 WSL2 的内核组件。Win11 的内核
# 走系统更新分发，Win10 必须单独装这个。目标机是 Win10 19045，需要。
#
# Debian 走 aka.ms 短链，最终落到微软 CDN。这条路径和 Windows Update
# 通道是两套基础设施，前者挂了不影响后者。
# ---------------------------------------------------------------------
$Artifacts = @(
    @{
        Name    = 'wsl_update_x64.msi'
        Url     = 'https://wslstorestorage.blob.core.windows.net/wslblob/wsl_update_x64.msi'
        MinSize = 5MB
        Purpose = 'WSL2 内核（Windows 10 必需）'
    },
    @{
        Name    = 'debian.appxbundle'
        Url     = 'https://aka.ms/wsl-debian-gnulinux'
        MinSize = 50MB
        Purpose = 'Debian 发行版'
    }
)


function Write-Step {
    param([string]$Message)
    Write-Host ""
    Write-Host "==> $Message" -ForegroundColor Cyan
}


function Test-Admin {
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($id)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}


function Invoke-Download {
    if (-not (Test-Path $Path)) {
        New-Item -ItemType Directory -Path $Path -Force | Out-Null
    }
    Write-Step "下载目录: $Path"

    $failed = @()
    foreach ($a in $Artifacts) {
        $dest = Join-Path $Path $a.Name

        # 已经下过且大小合理就跳过，方便中断后重跑
        if ((Test-Path $dest) -and (Get-Item $dest).Length -ge $a.MinSize) {
            $mb = (Get-Item $dest).Length / 1MB
            Write-Host ("    [跳过] {0,-22} 已存在 {1:N1} MB" -f $a.Name, $mb) -ForegroundColor DarkGray
            continue
        }

        Write-Host ("    [下载] {0,-22} {1}" -f $a.Name, $a.Purpose)

        # 优先用 curl.exe（Win10 1803 起内置）。Invoke-WebRequest 在 5.1 下
        # 又慢又没有可用的进度显示——关了进度条它完全静默，开着又每个 chunk
        # 重绘控制台慢一个数量级，几百 MB 的包下起来无法判断死活。
        # curl 有实时速度和 ETA，--continue-at - 还支持断点续传。
        $curl = Get-Command curl.exe -ErrorAction SilentlyContinue
        if ($curl) {
            # --fail：HTTP >= 400 直接返回非零，不把错误页写进文件
            $curlArgs = @(
                '-L', '--fail', '--retry', '3', '--retry-delay', '2',
                '--continue-at', '-', '-o', $dest, $a.Url
            )
            & $curl.Source @curlArgs
            $ok = ($LASTEXITCODE -eq 0)
            if (-not $ok) {
                Write-Host ("           失败: curl 退出码 {0}" -f $LASTEXITCODE) -ForegroundColor Red
            }
        } else {
            try {
                Invoke-WebRequest -Uri $a.Url -OutFile $dest -UseBasicParsing -TimeoutSec 600
                $ok = $true
            } catch {
                Write-Host ("           失败: {0}" -f $_.Exception.Message) -ForegroundColor Red
                $ok = $false
            }
        }
        if (-not $ok) { $failed += $a.Name; continue }

        $size = (Get-Item $dest).Length
        if ($size -lt $a.MinSize) {
            # 拿到的多半是错误页而不是安装包
            Write-Host ("           异常: 只有 {0:N1} MB，疑似下到了错误页" -f ($size / 1MB)) -ForegroundColor Red
            Remove-Item $dest -Force
            $failed += $a.Name
            continue
        }
        Write-Host ("           完成 {0:N1} MB" -f ($size / 1MB)) -ForegroundColor Green
    }

    Write-Step "下载结果"
    if ($failed.Count -gt 0) {
        Write-Host "    以下项目失败，重跑本脚本会只重试它们：" -ForegroundColor Yellow
        $failed | ForEach-Object { Write-Host "      - $_" -ForegroundColor Yellow }
        Write-Host ""
        Write-Host "    若反复失败，可用浏览器手动下载后放进 $Path：" -ForegroundColor Yellow
        $Artifacts | Where-Object { $failed -contains $_.Name } | ForEach-Object {
            Write-Host "      $($_.Name)" -ForegroundColor Yellow
            Write-Host "        $($_.Url)" -ForegroundColor DarkGray
        }
        return $false
    }

    Get-ChildItem $Path | Select-Object Name, @{n='MB';e={[math]::Round($_.Length/1MB,1)}} | Format-Table -AutoSize
    Write-Host "    全部就绪。重启后运行：" -ForegroundColor Green
    Write-Host "      .\Prep-WslOffline.ps1 -Install" -ForegroundColor Green
    return $true
}


function Invoke-Install {
    if (-not (Test-Admin)) {
        throw "需要管理员权限。请用管理员身份重开 PowerShell 再运行。"
    }

    # 组件必须已启用且重启过，否则装了内核也起不来
    Write-Step "检查前置组件"
    $features = @{
        'VirtualMachinePlatform'            = '虚拟机平台'
        'Microsoft-Windows-Subsystem-Linux' = '适用于 Linux 的 Windows 子系统'
    }
    $missing = @()
    foreach ($key in $features.Keys) {
        $state = (Get-WindowsOptionalFeature -Online -FeatureName $key).State
        $mark = if ($state -eq 'Enabled') { '✓' } else { '✗' }
        Write-Host "    $mark $($features[$key]): $state"
        if ($state -ne 'Enabled') { $missing += $features[$key] }
    }
    if ($missing.Count -gt 0) {
        throw "组件未启用: $($missing -join ', ')。先跑 wsl --install --no-distribution 再重启。"
    }

    # 装 WSL2 内核
    Write-Step "安装 WSL2 内核"
    $msi = Join-Path $Path 'wsl_update_x64.msi'
    if (-not (Test-Path $msi)) { throw "找不到 $msi，先跑 -Download。" }
    $p = Start-Process msiexec.exe -ArgumentList "/i `"$msi`" /quiet /norestart" -Wait -PassThru
    # 3010 = 成功但需要重启，不算失败
    if ($p.ExitCode -notin @(0, 3010)) {
        throw "msiexec 返回 $($p.ExitCode)"
    }
    Write-Host "    完成 (exit $($p.ExitCode))" -ForegroundColor Green

    wsl --set-default-version 2 | Out-Null

    # 装 Debian
    Write-Step "安装 Debian"
    $appx = Join-Path $Path 'debian.appxbundle'
    if (-not (Test-Path $appx)) { throw "找不到 $appx，先跑 -Download。" }

    # 失败重跑时包已经注册过，再 Add 一次会抛异常中断整个流程
    $existing = Get-AppxPackage -Name '*Debian*' -ErrorAction SilentlyContinue |
                Select-Object -First 1
    if ($existing) {
        Write-Host "    已注册: $($existing.Name) $($existing.Version)，跳过" -ForegroundColor DarkGray
    } else {
        Add-AppxPackage -Path $appx
        Write-Host "    包已注册" -ForegroundColor Green
    }

    # Appx 装完只是把 debian.exe 放到 WindowsApps，发行版还没落地。
    # `install --root` 跳过创建普通用户的交互提问——救援盘构建全程用 root，
    # 不需要普通用户，正好省掉一次人工输入。
    Write-Step "初始化 Debian 根文件系统"
    $debian = Get-Command debian.exe -ErrorAction SilentlyContinue
    if (-not $debian) {
        throw "找不到 debian.exe。请手动从开始菜单启动一次 Debian 完成初始化。"
    }

    & $debian.Source install --root
    $initExit = $LASTEXITCODE
    if ($initExit -ne 0) {
        Write-Host ""
        Write-Host "    初始化失败（退出码 $initExit）" -ForegroundColor Red
        # 0x80370102 = 功能装了但 CPU 虚拟化没开。这个错误码的官方文案会
        # 让人以为要去装「虚拟机平台」，但真正的原因通常在 BIOS 或
        # hypervisorlaunchtype，所以在这里直接给出排查路径。
        Write-Host "    若错误码是 0x80370102，说明 CPU 虚拟化未启用。排查顺序：" -ForegroundColor Yellow
        Write-Host "      1. 任务管理器 → 性能 → CPU，看「虚拟化」是否已启用" -ForegroundColor Yellow
        Write-Host "      2. (Get-CimInstance Win32_Processor).VirtualizationFirmwareEnabled" -ForegroundColor Yellow
        Write-Host "         为 False → 进 BIOS 开启 Intel VT-x / AMD SVM" -ForegroundColor Yellow
        Write-Host "      3. 为 True 但仍失败 → bcdedit /set hypervisorlaunchtype auto，重启" -ForegroundColor Yellow
        Write-Host "      4. 仍失败 → 检查是否有 VMware / VirtualBox 占用 VT-x" -ForegroundColor Yellow
        Write-Host ""
        Write-Host "    修好后重跑本脚本 -Install 即可，已完成的步骤会自动跳过。" -ForegroundColor Yellow
        throw "Debian 根文件系统初始化失败"
    }

    Write-Step "结果"
    wsl -l -v
    Write-Host ""
    Write-Host "    接下来可以构建 ISO：" -ForegroundColor Green
    Write-Host "      cd kla\live; .\build.ps1" -ForegroundColor Green
}


if ($Install) { Invoke-Install } else { Invoke-Download | Out-Null }
