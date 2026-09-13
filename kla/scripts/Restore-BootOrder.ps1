<#
.SYNOPSIS
    把 UEFI 引导顺序还原成 KLA 部署之前的样子（risks.md R2 第 5 条）。

.DESCRIPTION
    KLA 部署时会把自己的 Boot#### 插到 BootOrder 最前面，开机默认进 GRUB。
    如果 GRUB 出了问题、或者用户就是想撤掉，本脚本把 BootOrder 写回
    %ProgramData%\KLA\bootorder.bak 里记录的原始字节。

    **这个脚本跑在 Windows 里。** 也就是说前提是你还进得去 Windows——
    如果 GRUB 坏到连菜单都出不来，先在开机时按 F12 / F9 / Esc（各家不同）
    调出固件启动菜单，手动选 Windows Boot Manager 进系统，再跑本脚本。
    固件启动菜单是硬件级的，不依赖 BootOrder，永远救得回来。

    做了什么：
      1. 读 %ProgramData%\KLA\bootorder.bak
      2. 校验：非空、长度偶数、里面的项还存在、至少有一个 Windows 启动管理器
      3. 把**当前**顺序另存为 bootorder.pre-restore.bak（还原这一步本身也可撤销）
      4. 写 BootOrder，再读回来逐字节比对
      5. 若 BootNext 指着一个不在还原后顺序里的项（多半就是 KLA），改指 Windows

    没做什么——都是刻意的：
      · 不删除、不修改任何 Boot#### 项。R2 第 1 条：只增不改。KLA 那个项还留着，
        它不在 BootOrder 里就不会被启动，几百字节 NVRAM 而已。留着还有个好处：
        以后重新启用 KLA 不用重新创建。
      · 不碰 Windows 的 BCD。GRUB 用 chainloader 直接指 bootmgfw.efi，
        BCD 从头到尾没被动过，没什么可还原的。

.PARAMETER BackupPath
    备份文件路径。默认 %ProgramData%\KLA\bootorder.bak。

.PARAMETER WindowsFirst
    不读备份，改成「把当前 BootOrder 里的 Windows 启动管理器挪到最前面」。
    备份丢了或者压根没备份过时的应急路径。只重排、不新增，比还原保守。

.PARAMETER KeepBootNext
    不动 BootNext。默认会在 BootNext 指向一个已不在引导顺序里的项时把它改指
    Windows——否则「还原完了下次开机还是进 GRUB」，等于没还原。

.PARAMETER Force
    跳过确认提示。

.EXAMPLE
    .\Restore-BootOrder.ps1 -WhatIf
    只看会改成什么样，不写固件。建议先跑这个。

.EXAMPLE
    .\Restore-BootOrder.ps1

.EXAMPLE
    .\Restore-BootOrder.ps1 -WindowsFirst -Force

.NOTES
    需要管理员权限（SE_SYSTEM_ENVIRONMENT_NAME 只有管理员能启用）。
#>
[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'High')]
param(
    [string] $BackupPath,
    [switch] $WindowsFirst,
    [switch] $KeepBootNext,
    [switch] $Force
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'KlaFirmware.ps1')

if ($Force) { $ConfirmPreference = 'None' }

function Write-Head { param([string]$Text) Write-Host ''; Write-Host "== $Text" -ForegroundColor Cyan }

function Format-Order {
    param([UInt16[]] $Order, [int] $Highlight = -1)
    $rows = @()
    $pos = 0
    foreach ($n in $Order) {
        $e = Get-BootEntry -Number $n
        if ($null -eq $e) {
            $desc = '(项不存在，固件会跳过)'
            $st   = '缺失'
        } else {
            $desc = $e.Description
            if ($e.Active) { $st = '启用' } else { $st = '停用' }
        }
        $mark = ''
        if (Test-WindowsBootEntry $e) { $mark = ' ← Windows' }
        $rows += [PSCustomObject]@{
            '#'  = $pos
            项   = 'Boot{0:X4}' -f $n
            状态 = $st
            名称 = $desc + $mark
        }
        $pos++
    }
    return $rows
}


Write-Head '特权与固件模式'
$bootCurrent = Assert-FirmwareAccess          # 失败直接 throw，不用逐项判断
Write-Host '  ✓ 特权已启用，UEFI 变量可读写' -ForegroundColor Green
Write-Host ("  本次启动来自: Boot{0:X4}" -f $bootCurrent)

$current = Get-BootOrder
if ($current.Count -eq 0) { throw '读不到 BootOrder，或者它是空的。' }


# --- 算出目标顺序 -----------------------------------------------------------
Write-Head '当前引导顺序'
Format-Order -Order $current | Format-Table -AutoSize

if ($WindowsFirst) {
    Write-Head '目标顺序（-WindowsFirst：只把 Windows 挪到最前，不新增项）'

    $win = @(); $rest = @()
    foreach ($n in $current) {
        if (Test-WindowsBootEntry (Get-BootEntry -Number $n)) { $win += $n } else { $rest += $n }
    }

    if ($win.Count -eq 0) {
        # BootOrder 里一个 Windows 项都没有——说明它被挤掉了或者被删了。
        # 扫低位编号找回来：绝大多数固件的 Boot#### 都落在 0x0000-0x00FF，
        # 全量扫 0x0000-0xFFFF 是 65536 次固件调用，没必要。
        Write-Host '  当前顺序里没有 Windows 项，扫描 Boot0000-Boot00FF 找回……' -ForegroundColor Yellow
        for ($i = 0; $i -le 0xFF; $i++) {
            if ($current -contains $i) { continue }
            $e = Get-BootEntry -Number $i
            if (Test-WindowsBootEntry $e) {
                Write-Host ("  找到 {0}: {1}" -f $e.Id, $e.Description) -ForegroundColor Green
                $win += [UInt16]$i
            }
        }
    }
    if ($win.Count -eq 0) { throw '整个 Boot0000-Boot00FF 里都找不到 Windows 启动管理器，无法安全重排。' }

    $target = @($win) + @($rest)
    $srcDesc = '-WindowsFirst 重排'
}
else {
    if (-not $BackupPath) { $BackupPath = Get-KlaBootOrderBackupPath }
    Write-Head "目标顺序（来自备份 $BackupPath）"
    if (-not (Test-Path -LiteralPath $BackupPath)) {
        Write-Host "  ✗ 备份文件不存在：$BackupPath" -ForegroundColor Red
        Write-Host '    说明 KLA 没在这台机器上部署过，或者备份被删了。' -ForegroundColor Red
        Write-Host '    应急路径：改用 -WindowsFirst，直接把 Windows 挪到启动顺序最前面。' -ForegroundColor Yellow
        return
    }
    $target  = Get-BootOrderBackup -Path $BackupPath      # 内含非空/偶数长度校验
    $srcDesc = "备份 $(Split-Path -Leaf $BackupPath)"
}

Format-Order -Order $target | Format-Table -AutoSize


# --- 写之前的四道闸 ---------------------------------------------------------
Write-Head '安全校验'

$missing = @()
foreach ($n in $target) { if ($null -eq (Get-BootEntry -Number $n)) { $missing += $n } }

if ($missing.Count -eq $target.Count) {
    throw "目标顺序里 $($target.Count) 个项**全部**已不存在，写进去等于让机器无项可启。拒绝执行。"
}
if ($missing.Count -gt 0) {
    # UEFI 规范要求固件跳过 BootOrder 里不存在的项，所以这不致命，但值得说一声：
    # 通常意味着备份是在一次固件升级或换盘之前做的。
    $names = ($missing | ForEach-Object { 'Boot{0:X4}' -f $_ }) -join ', '
    Write-Host "  ! 有 $($missing.Count) 个项已不存在（$names），固件会自动跳过" -ForegroundColor Yellow
} else {
    Write-Host '  ✓ 目标顺序里的项全部存在' -ForegroundColor Green
}

$hasWin = $false
foreach ($n in $target) { if (Test-WindowsBootEntry (Get-BootEntry -Number $n)) { $hasWin = $true; break } }
if ($hasWin) {
    Write-Host '  ✓ 目标顺序里有 Windows 启动管理器' -ForegroundColor Green
} else {
    # 这是最要命的一种：写完之后可能哪儿都进不去。宁可什么都不做。
    Write-Host '  ✗ 目标顺序里没有 Windows 启动管理器！' -ForegroundColor Red
    Write-Host '    写进去有可能导致开机进不了 Windows。改用 -WindowsFirst，' -ForegroundColor Red
    Write-Host '    或者确认你知道自己在做什么后加 -Force 重跑。' -ForegroundColor Red
    if (-not $Force) { return }
}

$curHex = ($current | ForEach-Object { '{0:X4}' -f $_ }) -join ','
$tgtHex = ($target  | ForEach-Object { '{0:X4}' -f $_ }) -join ','
if ($curHex -eq $tgtHex) {
    Write-Host '  ✓ 当前顺序和目标顺序完全一致，无需修改。' -ForegroundColor Green
    if (-not $KeepBootNext) { Write-Host '    （BootNext 仍会按下面的规则检查）' -ForegroundColor DarkGray }
    else { return }
}


# --- 落盘 -------------------------------------------------------------------
if ($curHex -ne $tgtHex) {
    if ($PSCmdlet.ShouldProcess('UEFI 变量 BootOrder', "还原为 $srcDesc（$tgtHex）")) {

        # 还原这一步本身也要可撤销：万一备份记的是个更糟的状态，得能退回来。
        # -Overwrite 是必须的——每次还原都该刷新这个快照，它记的是「还原前」，
        # 而 bootorder.bak 记的是「部署前」，两者用途不同，互不覆盖。
        $snap = Join-Path (Get-KlaDataDir) 'bootorder.pre-restore.bak'
        Save-BootOrderBackup -Path $snap -Reason 'Restore-BootOrder 执行前的快照' -Overwrite | Out-Null
        Write-Host "  当前顺序已快照到 $snap" -ForegroundColor DarkGray

        $bytes = New-Object byte[] ($target.Count * 2)
        for ($i = 0; $i -lt $target.Count; $i++) {
            [BitConverter]::GetBytes([UInt16]$target[$i]).CopyTo($bytes, $i * 2)
        }

        $err = 0
        if (-not [KlaFirmware]::Write('BootOrder', $bytes, [ref]$err)) {
            throw "写 BootOrder 失败，错误码 $err。固件未被修改。"
        }

        # 读回来比对。有些固件会静默拒绝或截断写入，只看返回值不够。
        $verify = Get-BootOrder
        $vHex = ($verify | ForEach-Object { '{0:X4}' -f $_ }) -join ','
        if ($vHex -ne $tgtHex) {
            throw "写回校验失败！期望 $tgtHex，实读 $vHex。用 $snap 手动核对。"
        }
        Write-Host '  ✓ BootOrder 已写入并校验通过' -ForegroundColor Green
    }
}


# --- BootNext ---------------------------------------------------------------
# 顺序还原了但 BootNext 还指着 KLA 的话，下次开机照样进 GRUB，用户会以为
# 还原没生效。UEFI 规范里 BootNext 被固件处理后会自动删除，所以把它改指
# Windows 是一次性的，不留痕。
if (-not $KeepBootNext) {
    Write-Head 'BootNext'
    $err = 0
    $bnRaw = [KlaFirmware]::Read('BootNext', [ref]$err)
    if ($null -eq $bnRaw) {
        Write-Host '  未设置（正常状态），无需处理' -ForegroundColor Green
    } else {
        $bn = [BitConverter]::ToUInt16($bnRaw, 0)
        if ($target -contains $bn) {
            Write-Host ("  BootNext = Boot{0:X4}，在还原后的顺序里，保持不动" -f $bn) -ForegroundColor Green
        } else {
            $first = $target[0]
            Write-Host ("  ! BootNext = Boot{0:X4}，不在还原后的顺序里（多半是 KLA）" -f $bn) -ForegroundColor Yellow
            if ($PSCmdlet.ShouldProcess('UEFI 变量 BootNext', ('改指 Boot{0:X4}' -f $first))) {
                if ([KlaFirmware]::Write('BootNext', [BitConverter]::GetBytes([UInt16]$first), [ref]$err)) {
                    Write-Host ("  ✓ BootNext 改指 Boot{0:X4}（下次开机后固件自动清除）" -f $first) -ForegroundColor Green
                } else {
                    # 不抛异常：BootOrder 已经还原成功了，这只是锦上添花的一步
                    Write-Host "  ! 改 BootNext 失败，错误码 $err。下次开机可能仍进 GRUB，" -ForegroundColor Yellow
                    Write-Host '    再开机一次就会按 BootOrder 走。' -ForegroundColor Yellow
                }
            }
        }
    }
}


Write-Head '完成'
Write-Host '  重启验证：开机应直接进 Windows。' -ForegroundColor Green
Write-Host '  KLA 的 Boot#### 项仍保留在固件里（只是不再被启动），' -ForegroundColor DarkGray
Write-Host '  以后要重新启用不必重建。' -ForegroundColor DarkGray
