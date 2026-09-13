<#
.SYNOPSIS
    UEFI 固件变量访问层，由 KLA 的各个引导相关脚本共用。

.DESCRIPTION
    本文件不单独执行，用点号引入：

        . (Join-Path $PSScriptRoot 'KlaFirmware.ps1')

    提供：
      [KlaFirmware]::EnableFirmwarePrivilege()   启用 SE_SYSTEM_ENVIRONMENT_NAME
      [KlaFirmware]::Read(name, [ref]err)        读全局 UEFI 变量
      [KlaFirmware]::Write(name, bytes, [ref]e)  写全局 UEFI 变量（拒绝空写，防误删）
      [KlaFirmware]::Delete(name, [ref]err)      删除全局 UEFI 变量（nSize=0，显式删）
      Assert-FirmwareAccess                      一次性完成特权 + UEFI 模式检查
      ConvertFrom-LoadOption                     解析 EFI_LOAD_OPTION
      Get-BootEntry / Get-BootOrder              读引导项与引导顺序
      Save-BootOrderBackup / Get-BootOrderBackup BootOrder 备份与读回
      Set-BootNext / Clear-BootNext              下次开机「只走一次」引导项

    抽出来是因为 Add-Type 在同一个 PowerShell 会话里加载同名类型会抛异常，
    两个脚本各自 Add-Type 一份的话，先后跑就会炸。这里用类型存在性守卫。
#>

if (-not ('KlaFirmware' -as [type])) {
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

public static class KlaFirmware
{
    // UEFI 全局变量命名空间。BootOrder / BootCurrent / BootNext / Boot#### 都在这里。
    public const string GlobalGuid = "{8be4df61-93ca-11d2-aa0d-00e098032b8c}";

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern uint GetFirmwareEnvironmentVariableW(
        string lpName, string lpGuid, byte[] pBuffer, uint nSize);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool SetFirmwareEnvironmentVariableW(
        string lpName, string lpGuid, byte[] pValue, uint nSize);

    [StructLayout(LayoutKind.Sequential)]
    public struct LUID { public uint LowPart; public int HighPart; }

    [StructLayout(LayoutKind.Sequential)]
    public struct LUID_AND_ATTRIBUTES { public LUID Luid; public uint Attributes; }

    [StructLayout(LayoutKind.Sequential)]
    public struct TOKEN_PRIVILEGES {
        public uint PrivilegeCount;
        public LUID_AND_ATTRIBUTES Privileges;
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool OpenProcessToken(IntPtr h, uint access, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool LookupPrivilegeValueW(string system, string name, out LUID luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll,
        ref TOKEN_PRIVILEGES newState, uint len, IntPtr prev, IntPtr retLen);

    [DllImport("kernel32.dll")]
    static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CloseHandle(IntPtr h);

    /// <summary>启用 SE_SYSTEM_ENVIRONMENT_NAME。没有它，读写固件变量一律失败。</summary>
    public static bool EnableFirmwarePrivilege(out int errorCode)
    {
        errorCode = 0;
        IntPtr token;
        // TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY
        if (!OpenProcessToken(GetCurrentProcess(), 0x20 | 0x8, out token)) {
            errorCode = Marshal.GetLastWin32Error();
            return false;
        }
        try {
            LUID luid;
            if (!LookupPrivilegeValueW(null, "SeSystemEnvironmentPrivilege", out luid)) {
                errorCode = Marshal.GetLastWin32Error();
                return false;
            }
            var tp = new TOKEN_PRIVILEGES {
                PrivilegeCount = 1,
                Privileges = new LUID_AND_ATTRIBUTES { Luid = luid, Attributes = 0x2 } // SE_PRIVILEGE_ENABLED
            };
            if (!AdjustTokenPrivileges(token, false, ref tp,
                    (uint)Marshal.SizeOf(typeof(TOKEN_PRIVILEGES)), IntPtr.Zero, IntPtr.Zero)) {
                errorCode = Marshal.GetLastWin32Error();
                return false;
            }
            // AdjustTokenPrivileges 即使部分失败也返回 true，必须查 LastError
            errorCode = Marshal.GetLastWin32Error();
            return errorCode == 0;   // ERROR_NOT_ALL_ASSIGNED = 1300
        } finally {
            CloseHandle(token);
        }
    }

    /// <summary>读一个全局 UEFI 变量。返回 null 表示不存在或读取失败。</summary>
    public static byte[] Read(string name, out int errorCode)
    {
        var buf = new byte[8192];
        uint n = GetFirmwareEnvironmentVariableW(name, GlobalGuid, buf, (uint)buf.Length);
        errorCode = (n == 0) ? Marshal.GetLastWin32Error() : 0;
        if (n == 0) return null;
        var result = new byte[n];
        Array.Copy(buf, result, (int)n);
        return result;
    }

    /// <summary>
    /// 写一个全局 UEFI 变量。
    /// 注意：nSize 传 0 会**删除**该变量，这是 UEFI 规范定义的行为，
    /// 所以调用方必须自己保证不会传空数组进来。
    /// </summary>
    public static bool Write(string name, byte[] value, out int errorCode)
    {
        if (value == null || value.Length == 0) {
            errorCode = 87;   // ERROR_INVALID_PARAMETER，绝不让空写变成删除
            return false;
        }
        bool ok = SetFirmwareEnvironmentVariableW(name, GlobalGuid, value, (uint)value.Length);
        errorCode = ok ? 0 : Marshal.GetLastWin32Error();
        return ok;
    }

    /// <summary>
    /// 删除一个全局 UEFI 变量。UEFI 规范：nSize=0 即删除。
    /// 与 Write 的区别：Write 故意拒绝空数组以防误删其他变量，Delete 显式删除。
    /// 用途：Clear-BootNext 把 BootNext 变量抹掉，让机器恢复常规 BootOrder 启动。
    /// </summary>
    public static bool Delete(string name, out int errorCode)
    {
        // pBuffer 传 NULL、nSize 传 0，是 UEFI 规范定义的删除路径。
        // P/Invoke 的 byte[] 在传入 null 时 marshal 成 NULL pointer，正合此意。
        bool ok = SetFirmwareEnvironmentVariableW(name, GlobalGuid, null, 0);
        errorCode = ok ? 0 : Marshal.GetLastWin32Error();
        return ok;
    }
}
'@
}


<#
.SYNOPSIS
    一次性完成「特权拿得到 + 这机器是 UEFI」两项检查。
.DESCRIPTION
    失败时直接 throw，调用方不必逐项判断。所有碰固件变量的脚本
    第一件事都应该是调它。
#>
function Assert-FirmwareAccess {
    $err = 0
    if (-not [KlaFirmware]::EnableFirmwarePrivilege([ref]$err)) {
        # 1300 = ERROR_NOT_ALL_ASSIGNED，基本就是没以管理员身份运行
        $hint = if ($err -eq 1300) { '（多半是没以管理员身份运行）' } else { '' }
        throw "无法启用 SE_SYSTEM_ENVIRONMENT_NAME，错误码 $err $hint"
    }

    $probe = [KlaFirmware]::Read('BootCurrent', [ref]$err)
    if ($null -eq $probe) {
        # 1 = ERROR_INVALID_FUNCTION，Windows 用它表示「这机器不是 UEFI 启动」
        if ($err -eq 1) {
            throw "这台机器是 Legacy BIOS 启动，没有 UEFI 变量，KLA 的引导方案不适用。"
        }
        throw "读 BootCurrent 失败，错误码 $err"
    }
    return [BitConverter]::ToUInt16($probe, 0)
}


# ---------------------------------------------------------------------
# EFI_LOAD_OPTION 解析
#
#   UINT32  Attributes
#   UINT16  FilePathListLength
#   CHAR16  Description[]          以 0x0000 结尾
#   EFI_DEVICE_PATH_PROTOCOL       FilePathList
#
# 设备路径是一串节点：UINT8 Type, UINT8 SubType, UINT16 Length。
# 只关心 Type=4(Media) SubType=4(File Path)，它才是 .efi 的路径。
# ---------------------------------------------------------------------
function ConvertFrom-LoadOption {
    param([byte[]]$Bytes)

    if ($null -eq $Bytes -or $Bytes.Length -lt 6) { return $null }

    $attributes = [BitConverter]::ToUInt32($Bytes, 0)
    $pathLen    = [BitConverter]::ToUInt16($Bytes, 4)

    # 描述字符串：从偏移 6 起的 UTF-16，读到 0x0000 为止
    $i = 6
    while ($i + 1 -lt $Bytes.Length) {
        if ($Bytes[$i] -eq 0 -and $Bytes[$i + 1] -eq 0) { break }
        $i += 2
    }
    $desc = [Text.Encoding]::Unicode.GetString($Bytes, 6, $i - 6)
    $dpStart = $i + 2

    # 遍历设备路径节点，捞出文件路径
    $files = @()
    $p = $dpStart
    $dpEnd = [Math]::Min($dpStart + $pathLen, $Bytes.Length)
    while ($p + 4 -le $dpEnd) {
        $type    = $Bytes[$p]
        $subType = $Bytes[$p + 1]
        $len     = [BitConverter]::ToUInt16($Bytes, $p + 2)
        if ($len -lt 4) { break }               # 长度非法，防死循环
        if ($type -eq 0x7F) { break }           # End of Device Path
        if ($type -eq 0x04 -and $subType -eq 0x04) {
            $strLen = $len - 4
            if ($strLen -gt 0 -and $p + 4 + $strLen -le $Bytes.Length) {
                $s = [Text.Encoding]::Unicode.GetString($Bytes, $p + 4, $strLen)
                $files += $s.TrimEnd([char]0)
            }
        }
        $p += $len
    }

    [PSCustomObject]@{
        Description = $desc
        # bit0 = LOAD_OPTION_ACTIVE
        Active      = [bool]($attributes -band 0x1)
        FilePath    = if ($files.Count) { $files -join ' ' } else { '(非文件路径，可能是网络/整盘启动)' }
    }
}


<#
.SYNOPSIS
    读 BootOrder，返回 UInt16 数组。变量不存在时返回空数组。
#>
function Get-BootOrder {
    $err = 0
    $raw = [KlaFirmware]::Read('BootOrder', [ref]$err)
    if ($null -eq $raw) { return @() }
    $order = @()
    for ($i = 0; $i + 1 -lt $raw.Length; $i += 2) {
        $order += [BitConverter]::ToUInt16($raw, $i)
    }
    return ,$order      # 前置逗号：防止单元素数组被 PowerShell 拆成标量
}


<#
.SYNOPSIS
    读单个 Boot#### 项并解析。不存在返回 $null。
.PARAMETER Number
    引导项编号（整数，不是 'Boot0001' 这种字符串）。
#>
function Get-BootEntry {
    param([int]$Number)

    $name = 'Boot{0:X4}' -f $Number
    $err = 0
    $raw = [KlaFirmware]::Read($name, [ref]$err)
    if ($null -eq $raw) { return $null }

    $opt = ConvertFrom-LoadOption -Bytes $raw
    [PSCustomObject]@{
        Number      = $Number
        Id          = $name
        Description = $opt.Description
        Active      = $opt.Active
        FilePath    = $opt.FilePath
    }
}


<#
.SYNOPSIS
    判断一个引导项是不是 Windows 启动管理器。
.DESCRIPTION
    优先看路径而不是名字：描述串由 OEM 填，中文机器上可能是「Windows 启动
    管理器」，也见过被改成机型名的。而 \EFI\Microsoft\Boot\bootmgfw.efi
    这个路径是微软固定的，跨语言跨厂商都一样。
#>
function Test-WindowsBootEntry {
    param($Entry)

    if ($null -eq $Entry) { return $false }
    if ($Entry.FilePath -match 'bootmgfw\.efi') { return $true }
    if ($Entry.Description -match 'Windows Boot Manager') { return $true }
    return $false
}


# ---------------------------------------------------------------------
# BootOrder 备份文件
#
# 格式在这里定义、也在这里消费。部署侧和还原侧都只调这两个函数——各写
# 一份解析的话，格式一改就对不上，而对不上的后果是还原路径失效，那正是
# 最不能失效的地方。
#
#   %ProgramData%\KLA\bootorder.bak    BootOrder 的原始字节，一字不改
#   %ProgramData%\KLA\bootorder.json   人看的旁注：时间、机器、每项的描述
#
# 为什么原始字节要单独存而不是只存 JSON：还原时要写回固件的就是这串字节。
# 从 JSON 里的编号重新拼一遍，等于在还原路径上多插一道自己写的序列化逻辑。
# JSON 只用来给人看和排查，坏了不影响还原。
# ---------------------------------------------------------------------

function Get-KlaDataDir {
    $d = Join-Path $env:ProgramData 'KLA'
    if (-not (Test-Path -LiteralPath $d)) {
        New-Item -ItemType Directory -Path $d -Force | Out-Null
    }
    return $d
}

function Get-KlaBootOrderBackupPath {
    return (Join-Path (Get-KlaDataDir) 'bootorder.bak')
}


<#
.SYNOPSIS
    把当前 BootOrder 备份到 %ProgramData%\KLA\bootorder.bak。
.DESCRIPTION
    risks.md R2 第 2 条。部署脚本在动 BootOrder **之前**必须调一次。
.PARAMETER Overwrite
    覆盖已有备份。默认不覆盖，理由见函数体内注释。
.PARAMETER Path
    另存到别处（Restore 用它写 pre-restore 快照）。默认走标准位置。
#>
function Save-BootOrderBackup {
    [CmdletBinding()]
    param(
        [string] $Reason = '',
        [string] $Path,
        [switch] $Overwrite
    )

    $err = 0
    $raw = [KlaFirmware]::Read('BootOrder', [ref]$err)
    if ($null -eq $raw -or $raw.Length -eq 0) {
        throw "读 BootOrder 失败（错误码 $err），拒绝写出空备份文件。"
    }

    if (-not $Path) { $Path = Get-KlaBootOrderBackupPath }

    # 已有备份**默认不覆盖**：第一次备份记的是「KLA 部署之前」的引导顺序，
    # 那是唯一有还原价值的状态。部署之后再存一次就把它盖成了「部署之后」，
    # 还原时还原了个寂寞。要重新基线化必须显式 -Overwrite。
    if ((Test-Path -LiteralPath $Path) -and (-not $Overwrite)) {
        Write-Verbose "已存在 $Path，保留原件（要覆盖用 -Overwrite）"
        return $Path
    }

    [IO.File]::WriteAllBytes($Path, $raw)

    # 旁注写不出来不算失败——原始字节已经落盘，还原路径不依赖它
    try {
        $entries = @()
        for ($i = 0; $i + 1 -lt $raw.Length; $i += 2) {
            $n = [BitConverter]::ToUInt16($raw, $i)
            $e = Get-BootEntry -Number $n
            $entries += [PSCustomObject]@{
                Id          = 'Boot{0:X4}' -f $n
                Description = if ($e) { $e.Description } else { '(项已不存在)' }
                FilePath    = if ($e) { $e.FilePath }    else { '' }
            }
        }
        [PSCustomObject]@{
            SavedAt     = (Get-Date).ToString('s')
            Computer    = $env:COMPUTERNAME
            Reason      = $Reason
            BootOrderHex= (($raw | ForEach-Object { '{0:X2}' -f $_ }) -join ' ')
            Entries     = $entries
        } | ConvertTo-Json -Depth 4 |
            Set-Content -LiteralPath ([IO.Path]::ChangeExtension($Path, '.json')) -Encoding UTF8
    } catch {
        Write-Verbose "旁注 JSON 写出失败（不影响还原）：$($_.Exception.Message)"
    }

    return $Path
}


<#
.SYNOPSIS
    读回备份文件，返回 UInt16 编号数组。文件不存在或内容非法时抛异常。
#>
function Get-BootOrderBackup {
    [CmdletBinding()]
    param([string] $Path)

    if (-not $Path) { $Path = Get-KlaBootOrderBackupPath }
    if (-not (Test-Path -LiteralPath $Path)) {
        throw "备份文件不存在：$Path"
    }

    $raw = [IO.File]::ReadAllBytes($Path)
    if ($raw.Length -eq 0) {
        throw "备份文件是空的：$Path —— 空 BootOrder 写进固件会让机器无项可启，拒绝使用。"
    }
    # BootOrder 是 UINT16 数组，字节数必须是偶数。奇数说明文件被截断或
    # 被当成文本处理过（比如被某个编辑器加了换行）。
    if ($raw.Length % 2 -ne 0) {
        throw "备份文件长度 $($raw.Length) 字节，不是偶数，BootOrder 已损坏：$Path"
    }

    $order = @()
    for ($i = 0; $i + 1 -lt $raw.Length; $i += 2) {
        $order += [BitConverter]::ToUInt16($raw, $i)
    }
    return ,$order
}


# ---------------------------------------------------------------------
# BootNext —— 下次开机「只走一次」的引导项指针
#
#   BootNext 是 UINT16，指向某 Boot#### 的编号。固件下次开机走它之后，
#   BootNext 会被固件自动清除——但稳妥起见，进完 ACCESS 回 Windows，
#   调一次 Clear-BootNext 兜底。
#
#   这是 risks.md R2-3 的配套：不动 BootOrder（不动 Windows 的常规启动
#   顺序），只通过 BootNext 一次性地把下次开机引到 KLA。即使脚本中途
#   失败或机器掉电，下次重启还是按原 BootOrder 走，不会变砖。
# ---------------------------------------------------------------------

<#
.SYNOPSIS
    写 BootNext 变量，下次开机只进指定引导项一次。
.PARAMETER Number
    引导项编号（整数，不是 'Boot0001' 这种字符串）。
#>
function Set-BootNext {
    [CmdletBinding()]
    param([Parameter(Mandatory)][int]$Number)

    $err = 0
    $bytes = [BitConverter]::GetBytes([uint16]$Number)
    if (-not [KlaFirmware]::Write('BootNext', $bytes, [ref]$err)) {
        throw "写 BootNext 失败（错误码 $err）"
    }
}


<#
.SYNOPSIS
    删除 BootNext 变量，恢复常规引导顺序。
.DESCRIPTION
    UEFI 规范：nSize=0 即删除变量。KlaFirmware.Write 故意拒绝空写以防
    误删其他变量，这里走 KlaFirmware.Delete 显式删。
    删除一个不存在的变量在 Windows 上返回 ERROR_NOT_FOUND (203)，
    视作成功——目标状态已达成。
#>
function Clear-BootNext {
    [CmdletBinding()]
    param()

    $err = 0
    if (-not [KlaFirmware]::Delete('BootNext', [ref]$err)) {
        # 203 = ERROR_NOT_FOUND：变量本来就不在，目标状态已达成
        if ($err -ne 203) {
            throw "清除 BootNext 失败（错误码 $err）"
        }
    }
}
