<#
.SYNOPSIS
    KlaFirmware.ps1 里那几个二进制解析函数的单元测试。**不需要管理员权限。**

.DESCRIPTION
    ConvertFrom-LoadOption 手工解析 EFI_LOAD_OPTION，Get-BootOrderBackup 手工
    解析 BootOrder 字节流。这两处都是「差一个字节就默默出错」的地方——解析歪了
    不会抛异常，只会给出一个看起来挺合理的错误结果，然后被写进固件。

    真机上没法测：读固件要管理员权限，而且真机上只有一份数据，覆盖不到
    边界情况（空文件、奇数长度、描述串为空、非文件型设备路径……）。
    所以这里用合成的字节流跑，覆盖正常和异常两侧。

.EXAMPLE
    .\Test-KlaFirmware.ps1
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'KlaFirmware.ps1')

$script:Pass = 0
$script:Fail = 0
function It {
    param([string]$Name, [scriptblock]$Body)
    try {
        $r = & $Body
        if ($r) { Write-Host "  [OK]   $Name" -ForegroundColor Green; $script:Pass++ }
        else    { Write-Host "  [FAIL] $Name" -ForegroundColor Red;   $script:Fail++ }
    } catch {
        Write-Host "  [FAIL] $Name  —— $($_.Exception.Message)" -ForegroundColor Red
        $script:Fail++
    }
}

# ---------------------------------------------------------------------
# 合成一个 EFI_LOAD_OPTION
#
#   UINT32  Attributes
#   UINT16  FilePathListLength
#   CHAR16  Description[]        以 0x0000 结尾
#   BYTE[]  FilePathList         设备路径节点串
# ---------------------------------------------------------------------
function New-LoadOption {
    param(
        [string] $Description,
        [string] $FilePath,
        [UInt32] $Attributes = 1,
        [switch] $NoFileNode      # 造一个「非文件型」设备路径，模拟网络/整盘启动项
    )

    $desc = [Text.Encoding]::Unicode.GetBytes($Description) + @(0, 0)

    $dp = @()
    if (-not $NoFileNode) {
        $pathBytes = [Text.Encoding]::Unicode.GetBytes($FilePath) + @(0, 0)
        $nodeLen   = 4 + $pathBytes.Length
        # Type=0x04 (Media), SubType=0x04 (File Path)
        $dp += @(0x04, 0x04) + [BitConverter]::GetBytes([UInt16]$nodeLen) + $pathBytes
    }
    # End of Device Path: Type=0x7F, SubType=0xFF, Length=4
    $dp += @(0x7F, 0xFF, 0x04, 0x00)

    $bytes = [BitConverter]::GetBytes($Attributes) +
             [BitConverter]::GetBytes([UInt16]$dp.Length) +
             $desc + $dp
    return [byte[]]$bytes
}


Write-Host ''
Write-Host '== ConvertFrom-LoadOption' -ForegroundColor Cyan

$wb = New-LoadOption -Description 'Windows Boot Manager' `
                     -FilePath '\EFI\Microsoft\Boot\bootmgfw.efi'
$w  = ConvertFrom-LoadOption -Bytes $wb

It '描述串解析正确'      { $w.Description -eq 'Windows Boot Manager' }
It '文件路径解析正确'    { $w.FilePath -eq '\EFI\Microsoft\Boot\bootmgfw.efi' }
It 'Active 位解析正确'   { $w.Active -eq $true }

# 停用项：Attributes bit0 = 0
$off = ConvertFrom-LoadOption -Bytes (New-LoadOption -Description 'X' -FilePath '\a.efi' -Attributes 0)
It '停用项的 Active 为 false' { $off.Active -eq $false }

# 中文描述——OEM 在中文机器上真的会这么填，UTF-16 宽字符不能按字节数算
$cn = ConvertFrom-LoadOption -Bytes (New-LoadOption -Description 'Windows 启动管理器' -FilePath '\EFI\Microsoft\Boot\bootmgfw.efi')
It '中文描述串不被截断'  { $cn.Description -eq 'Windows 启动管理器' }

# 空描述：偏移 6 处直接就是 0x0000，最容易写出差一个字节的地方
$empty = ConvertFrom-LoadOption -Bytes (New-LoadOption -Description '' -FilePath '\EFI\boot\bootx64.efi')
It '空描述串不会把路径吞掉' { $empty.Description -eq '' -and $empty.FilePath -eq '\EFI\boot\bootx64.efi' }

# 非文件型设备路径（PXE、整盘启动项），不能崩也不能瞎猜
$nf = ConvertFrom-LoadOption -Bytes (New-LoadOption -Description 'PXE IPv4' -FilePath '' -NoFileNode)
It '非文件型设备路径不崩'   { $nf.Description -eq 'PXE IPv4' -and $nf.FilePath -match '非文件路径' }

# 截断/垃圾输入不能抛异常——固件里真的会有残留的坏项
It 'null 输入返回 null'     { $null -eq (ConvertFrom-LoadOption -Bytes $null) }
It '过短输入返回 null'      { $null -eq (ConvertFrom-LoadOption -Bytes ([byte[]]@(1,2,3))) }
It '截断输入不抛异常'       { $null -ne (ConvertFrom-LoadOption -Bytes $wb[0..20]) }


Write-Host ''
Write-Host '== Test-WindowsBootEntry' -ForegroundColor Cyan

It '认路径 bootmgfw.efi'    { Test-WindowsBootEntry ([PSCustomObject]@{ Description='随便什么名字'; FilePath='\EFI\Microsoft\Boot\bootmgfw.efi' }) }
It '认描述 Windows Boot Manager' { Test-WindowsBootEntry ([PSCustomObject]@{ Description='Windows Boot Manager'; FilePath='' }) }
# 中文机器上描述是「Windows 启动管理器」，只能靠路径认出来——这正是优先看路径的理由
It '中文描述靠路径认出'     { Test-WindowsBootEntry ([PSCustomObject]@{ Description='Windows 启动管理器'; FilePath='\EFI\Microsoft\Boot\bootmgfw.efi' }) }
It '不误认 GRUB'            { -not (Test-WindowsBootEntry ([PSCustomObject]@{ Description="KARL'S LIGHT ACCESS"; FilePath='\EFI\kla\grubx64.efi' })) }
It '不误认 Debian'          { -not (Test-WindowsBootEntry ([PSCustomObject]@{ Description='debian'; FilePath='\EFI\debian\shimx64.efi' })) }
It 'null 不崩'              { -not (Test-WindowsBootEntry $null) }


Write-Host ''
Write-Host '== Get-BootOrderBackup' -ForegroundColor Cyan

$tmp = Join-Path $env:TEMP ("kla-test-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $tmp -Force | Out-Null
try {
    # 正常：Boot0002, Boot0000, Boot0001（小端）
    $good = Join-Path $tmp 'good.bak'
    [IO.File]::WriteAllBytes($good, [byte[]]@(0x02,0x00, 0x00,0x00, 0x01,0x00))
    $o = Get-BootOrderBackup -Path $good
    It '解析出 3 项'        { $o.Count -eq 3 }
    It '小端序正确'          { $o[0] -eq 2 -and $o[1] -eq 0 -and $o[2] -eq 1 }

    # 单项：前置逗号防拆包，漏了的话 .Count 会变成 $null
    $one = Join-Path $tmp 'one.bak'
    [IO.File]::WriteAllBytes($one, [byte[]]@(0x05,0x00))
    $o1 = Get-BootOrderBackup -Path $one
    It '单项不被拆成标量'    { $o1.Count -eq 1 -and $o1[0] -eq 5 }

    # 高位编号，验证没被当成有符号数
    $hi = Join-Path $tmp 'hi.bak'
    [IO.File]::WriteAllBytes($hi, [byte[]]@(0xFF,0xFF))
    It 'Boot FFFF 不变负数'  { (Get-BootOrderBackup -Path $hi)[0] -eq 65535 }

    # 空文件必须拒绝：空 BootOrder 写进固件 = 无项可启
    $z = Join-Path $tmp 'zero.bak'
    [IO.File]::WriteAllBytes($z, [byte[]]@())
    It '空文件被拒绝'        { try { Get-BootOrderBackup -Path $z; $false } catch { $true } }

    # 奇数长度 = 文件被截断或被文本编辑器动过
    $odd = Join-Path $tmp 'odd.bak'
    [IO.File]::WriteAllBytes($odd, [byte[]]@(0x01,0x00,0x02))
    It '奇数长度被拒绝'      { try { Get-BootOrderBackup -Path $odd; $false } catch { $true } }

    It '文件不存在时抛异常'  { try { Get-BootOrderBackup -Path (Join-Path $tmp 'nope.bak'); $false } catch { $true } }
}
finally {
    Remove-Item -LiteralPath $tmp -Recurse -Force -ErrorAction SilentlyContinue
}


Write-Host ''
Write-Host '================================'
Write-Host ("  通过 {0} 项，失败 {1} 项" -f $script:Pass, $script:Fail)
Write-Host '================================'
if ($script:Fail -gt 0) { exit 1 }
