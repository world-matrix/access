<#
.SYNOPSIS
    阶段 0：KLA 的 Hyper-V 沙盘，用于在不碰真机的前提下验证分区与引导操作。

.DESCRIPTION
    改分区表和 UEFI 引导项是本项目唯一可能把机器搞成砖的部分（见
    docs/risks.md R1/R2）。所有此类操作必须先在这个沙盘里跑通。

    沙盘 VM 刻意做成 Gen2 + UEFI + 可切换 Secure Boot，以便复现真机的
    shim/MOK 行为。

.PARAMETER Action
    Create   建立沙盘 VM（幂等，已存在则跳过）
    Snapshot 打一个检查点
    Rollback 回滚到指定检查点（默认最近的 baseline）
    Start    启动 VM 并打开连接窗口
    Status   显示 VM 与检查点状态
    Remove   彻底删除 VM 和虚拟磁盘

.EXAMPLE
    .\Phase0-VmSandbox.ps1 -Action Create -WindowsIso D:\iso\win11.iso
    .\Phase0-VmSandbox.ps1 -Action Snapshot -Name "干净的 Windows"
    .\Phase0-VmSandbox.ps1 -Action Rollback -Name "干净的 Windows"
#>
[CmdletBinding()]
param(
    [ValidateSet('Create', 'Snapshot', 'Rollback', 'Start', 'Status', 'Remove')]
    [string] $Action = 'Status',

    [string] $VmName      = 'KLA-Sandbox',
    [string] $Name,
    [string] $WindowsIso,
    [string] $KlaIso,
    [int]    $DiskSizeGB  = 128,
    [int]    $MemoryGB    = 4,
    [int]    $Cpus        = 4,
    [switch] $SecureBoot
)

$ErrorActionPreference = 'Stop'

function Write-Step($m) { Write-Host "==> $m" -ForegroundColor Cyan }
function Write-Warn($m) { Write-Host "!!  $m" -ForegroundColor Yellow }
function Write-Ok($m)   { Write-Host "    $m" -ForegroundColor Green }

# --- 前置检查 ---------------------------------------------------------------
$hv = Get-WindowsOptionalFeature -Online -FeatureName Microsoft-Hyper-V-All -ErrorAction SilentlyContinue
if (-not $hv -or $hv.State -ne 'Enabled') {
    throw "Hyper-V 未启用。以管理员身份运行：Enable-WindowsOptionalFeature -Online -FeatureName Microsoft-Hyper-V-All -All"
}

$isAdmin = ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()
           ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) { throw "需要管理员权限运行。" }

$vmRoot  = Join-Path $env:ProgramData 'KLA\sandbox'
$vhdPath = Join-Path $vmRoot "$VmName.vhdx"
$vm      = Get-VM -Name $VmName -ErrorAction SilentlyContinue

# --- Create -----------------------------------------------------------------
if ($Action -eq 'Create') {
    if ($vm) { Write-Warn "VM '$VmName' 已存在，跳过创建。"; $Action = 'Status' }
    else {
        Write-Step "创建沙盘 VM '$VmName'"
        New-Item -ItemType Directory -Force -Path $vmRoot | Out-Null

        $switch = Get-VMSwitch -SwitchType External -ErrorAction SilentlyContinue |
                  Select-Object -First 1
        if (-not $switch) {
            $switch = Get-VMSwitch -ErrorAction SilentlyContinue | Select-Object -First 1
        }
        if (-not $switch) {
            Write-Warn "没有可用的虚拟交换机，VM 将无网络。救援环境的联网功能无法测试。"
        }

        $newVmArgs = @{
            Name               = $VmName
            Generation         = 2
            MemoryStartupBytes = $MemoryGB * 1GB
            NewVHDPath         = $vhdPath
            NewVHDSizeBytes    = $DiskSizeGB * 1GB
            Path               = $vmRoot
        }
        if ($switch) { $newVmArgs.SwitchName = $switch.Name }

        New-VM @newVmArgs | Out-Null
        Set-VMProcessor -VMName $VmName -Count $Cpus
        Set-VMMemory   -VMName $VmName -DynamicMemoryEnabled $false
        # 检查点必须是标准型：生产型检查点走 VSS，会让客机磁盘状态和我们
        # 要验证的分区表操作对不上号
        Set-VM -Name $VmName -CheckpointType Standard -AutomaticCheckpointsEnabled $false

        if ($SecureBoot) {
            # Linux 客机必须用 MicrosoftUEFICertificateAuthority 模板，
            # 用默认的 MicrosoftWindows 模板 shim 会被拒
            Set-VMFirmware -VMName $VmName -EnableSecureBoot On `
                           -SecureBootTemplate MicrosoftUEFICertificateAuthority
            Write-Ok "Secure Boot 已开启（UEFI CA 模板，可测 shim/MOK）"
        } else {
            Set-VMFirmware -VMName $VmName -EnableSecureBoot Off
            Write-Ok "Secure Boot 已关闭"
        }

        Write-Ok "VM 已创建：$DiskSizeGB GB 磁盘 / $MemoryGB GB 内存 / $Cpus 核"
        $vm = Get-VM -Name $VmName
    }
}

if (-not $vm) {
    Write-Warn "VM '$VmName' 不存在。先运行：.\Phase0-VmSandbox.ps1 -Action Create"
    return
}

# --- 挂载 ISO（Create 与 Start 都适用）--------------------------------------
if ($Action -in 'Create', 'Start') {
    foreach ($pair in @(@{Path = $WindowsIso; Label = 'Windows'},
                        @{Path = $KlaIso;     Label = 'KLA'})) {
        if (-not $pair.Path) { continue }
        if (-not (Test-Path $pair.Path)) { Write-Warn "$($pair.Label) ISO 不存在：$($pair.Path)"; continue }
        $full = (Resolve-Path $pair.Path).Path
        $already = Get-VMDvdDrive -VMName $VmName | Where-Object { $_.Path -eq $full }
        if (-not $already) {
            Add-VMDvdDrive -VMName $VmName -Path $full
            Write-Ok "已挂载 $($pair.Label) ISO：$full"
        }
    }
    # 有光盘时把光驱放到启动顺序最前，否则空盘 VM 会直接进 UEFI shell
    $dvd = Get-VMDvdDrive -VMName $VmName | Select-Object -First 1
    if ($dvd -and $dvd.Path) {
        $hdd = Get-VMHardDiskDrive -VMName $VmName
        Set-VMFirmware -VMName $VmName -BootOrder $dvd, $hdd
    }
}

# --- Snapshot ---------------------------------------------------------------
if ($Action -eq 'Snapshot') {
    $label = if ($Name) { $Name } else { 'baseline' }
    Write-Step "打检查点：$label"
    Checkpoint-VM -Name $VmName -SnapshotName $label
    Write-Ok "已保存。回滚用：-Action Rollback -Name `"$label`""
}

# --- Rollback ---------------------------------------------------------------
if ($Action -eq 'Rollback') {
    $checkpoints = Get-VMCheckpoint -VMName $VmName -ErrorAction SilentlyContinue
    if (-not $checkpoints) { throw "VM '$VmName' 没有任何检查点。" }

    $target = if ($Name) {
        $checkpoints | Where-Object { $_.Name -eq $Name } | Select-Object -First 1
    } else {
        $checkpoints | Sort-Object CreationTime -Descending | Select-Object -First 1
    }
    if (-not $target) {
        throw "找不到检查点 '$Name'。现有：$(($checkpoints.Name) -join ', ')"
    }

    if ($vm.State -eq 'Running') { Stop-VM -Name $VmName -TurnOff -Force }
    Write-Step "回滚到：$($target.Name)  ($($target.CreationTime))"
    Restore-VMCheckpoint -VMCheckpoint $target -Confirm:$false
    Write-Ok '回滚完成'
}

# --- Start ------------------------------------------------------------------
if ($Action -eq 'Start') {
    if ((Get-VM -Name $VmName).State -ne 'Running') { Start-VM -Name $VmName }
    Write-Ok "VM 已启动，打开连接窗口"
    Start-Process vmconnect.exe -ArgumentList $env:COMPUTERNAME, $VmName
}

# --- Remove -----------------------------------------------------------------
if ($Action -eq 'Remove') {
    Write-Warn "即将删除 VM '$VmName' 及其虚拟磁盘。"
    $confirm = Read-Host "确认删除？输入 VM 名称以继续"
    if ($confirm -ne $VmName) { Write-Warn '已取消。'; return }

    if ((Get-VM -Name $VmName).State -ne 'Off') { Stop-VM -Name $VmName -TurnOff -Force }
    $disks = Get-VMHardDiskDrive -VMName $VmName | Select-Object -ExpandProperty Path
    Remove-VM -Name $VmName -Force
    foreach ($d in $disks) { if (Test-Path $d) { Remove-Item $d -Force } }
    Write-Ok '已删除'
    return
}

# --- Status -----------------------------------------------------------------
Write-Host ''
Write-Step "沙盘状态：$VmName"
$vm = Get-VM -Name $VmName
$vm | Select-Object Name, State, CPUUsage,
      @{n='内存GB'; e={[math]::Round($_.MemoryAssigned/1GB,1)}},
      @{n='运行时长'; e={$_.Uptime}} | Format-Table -AutoSize

$fw = Get-VMFirmware -VMName $VmName
Write-Host "    Secure Boot : $($fw.SecureBoot)  [$($fw.SecureBootTemplate)]"

$dvds = Get-VMDvdDrive -VMName $VmName | Where-Object { $_.Path }
if ($dvds) {
    Write-Host "    已挂载 ISO  :"
    $dvds | ForEach-Object { Write-Host "                  $($_.Path)" }
} else {
    Write-Host "    已挂载 ISO  : 无"
}

$cps = Get-VMCheckpoint -VMName $VmName -ErrorAction SilentlyContinue
if ($cps) {
    Write-Host "    检查点      :"
    $cps | Sort-Object CreationTime |
        ForEach-Object { Write-Host "                  $($_.CreationTime.ToString('MM-dd HH:mm'))  $($_.Name)" }
} else {
    Write-Host "    检查点      : 无（建议装完 Windows 后立刻打一个）"
}
Write-Host ''
