<#
.SYNOPSIS
    只读探测本机 UEFI 引导配置，验证 KLA 软触发方案在这台机器上可行。

.DESCRIPTION
    KLA 的「软蓝键」——Windows 里点还原后自动重启进 ACCESS——依赖写
    UEFI 全局变量 BootNext。这条路能不能走通，取决于三件事：

      1. 机器是 UEFI 启动（不是 Legacy BIOS）
      2. 进程能拿到 SE_SYSTEM_ENVIRONMENT_NAME 特权
      3. GetFirmwareEnvironmentVariableW / SetFirmwareEnvironmentVariableW 可用

    本脚本验证前两件，并把第三件的**读**方向跑通。写方向要等部署时
    真的有 KLA 引导项才有意义，但读能跑通，写就只差权限之外的东西了。

    顺带把现有的 Boot#### 项全列出来。这是 risks.md R2 的前置：KLA
    绝不修改或删除已有引导项，只新增——那就得先知道已有哪些。

    **本脚本只读，不修改任何固件变量。**

.NOTES
    需要管理员权限（SE_SYSTEM_ENVIRONMENT_NAME 只有管理员能启用）。
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

public static class KlaFirmware
{
    // UEFI 全局变量命名空间。BootOrder / BootCurrent / BootNext / Boot#### 都在这里。
    public const string GlobalGuid = "{8be4df61-93ca-11d2-aa0d-00e098032b8c}";

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern uint GetFirmwareEnvironmentVariableW(
        string lpName, string lpGuid, byte[] pBuffer, uint nSize);

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
}
'@

function Write-Head {
    param([string]$Text)
    Write-Host ""
    Write-Host "== $Text" -ForegroundColor Cyan
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
# 我们只关心 Type=4(Media) SubType=4(File Path)，它才是 .efi 的路径。
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


Write-Head "特权与固件模式"

$err = 0
$ok = [KlaFirmware]::EnableFirmwarePrivilege([ref]$err)
if ($ok) {
    Write-Host "  ✓ SE_SYSTEM_ENVIRONMENT_NAME 已启用" -ForegroundColor Green
} else {
    # 1300 = ERROR_NOT_ALL_ASSIGNED，基本就是没以管理员身份运行
    $hint = if ($err -eq 1300) { "（多半是没以管理员身份运行）" } else { "" }
    Write-Host "  ✗ 特权启用失败，错误码 $err $hint" -ForegroundColor Red
    Write-Host "    没有这个特权，KLA 的软触发（BootNext）无法工作。" -ForegroundColor Red
    return
}

$bootCurrentRaw = [KlaFirmware]::Read('BootCurrent', [ref]$err)
if ($null -eq $bootCurrentRaw) {
    # 1 = ERROR_INVALID_FUNCTION，Windows 用它表示「这机器不是 UEFI 启动」
    if ($err -eq 1) {
        Write-Host "  ✗ 这台机器是 Legacy BIOS 启动，没有 UEFI 变量。" -ForegroundColor Red
        Write-Host "    KLA 的 BootNext 软触发不适用，只能靠 GRUB 热键。" -ForegroundColor Red
    } else {
        Write-Host "  ✗ 读 BootCurrent 失败，错误码 $err" -ForegroundColor Red
    }
    return
}
Write-Host "  ✓ UEFI 固件变量可读" -ForegroundColor Green

$bootCurrent = [BitConverter]::ToUInt16($bootCurrentRaw, 0)
Write-Host ("  本次启动来自: Boot{0:X4}" -f $bootCurrent)

$bootNextRaw = [KlaFirmware]::Read('BootNext', [ref]$err)
if ($bootNextRaw) {
    Write-Host ("  BootNext 当前已设为: Boot{0:X4}（下次启动生效后自动清除）" -f `
        [BitConverter]::ToUInt16($bootNextRaw, 0)) -ForegroundColor Yellow
} else {
    Write-Host "  BootNext 未设置（正常状态）" -ForegroundColor DarkGray
}


Write-Head "引导顺序 BootOrder"

$orderRaw = [KlaFirmware]::Read('BootOrder', [ref]$err)
if ($null -eq $orderRaw) {
    Write-Host "  读取失败，错误码 $err" -ForegroundColor Red
    return
}

$order = @()
for ($i = 0; $i + 1 -lt $orderRaw.Length; $i += 2) {
    $order += [BitConverter]::ToUInt16($orderRaw, $i)
}
Write-Host ("  " + (($order | ForEach-Object { 'Boot{0:X4}' -f $_ }) -join ' → '))
Write-Host ("  原始字节（{0} 字节，部署前会备份到 %ProgramData%\KLA\bootorder.bak）:" -f $orderRaw.Length) -ForegroundColor DarkGray
Write-Host ("  " + (($orderRaw | ForEach-Object { '{0:X2}' -f $_ }) -join ' ')) -ForegroundColor DarkGray


Write-Head "现有引导项（KLA 只新增，绝不改动下列任何一项）"

$rows = @()
foreach ($n in $order) {
    $name = 'Boot{0:X4}' -f $n
    $raw = [KlaFirmware]::Read($name, [ref]$err)
    if ($null -eq $raw) {
        $rows += [PSCustomObject]@{ 项 = $name; 状态 = "读取失败($err)"; 名称 = ''; 路径 = '' }
        continue
    }
    $opt = ConvertFrom-LoadOption -Bytes $raw
    $rows += [PSCustomObject]@{
        项     = $name + $(if ($n -eq $bootCurrent) { ' *' } else { '' })
        状态   = if ($opt.Active) { '启用' } else { '停用' }
        名称   = $opt.Description
        路径   = $opt.FilePath
    }
}
$rows | Format-Table -AutoSize -Wrap

Write-Host "  带 * 的是本次启动使用的项。" -ForegroundColor DarkGray


Write-Head "结论"
Write-Host "  ✓ 特权可获取、UEFI 变量可读、引导项可枚举" -ForegroundColor Green
Write-Host "    → KLA 软触发（写 BootNext 后重启进 ACCESS）方案在这台机器上成立。"
Write-Host "    → 写方向要等部署时真有 KLA 引导项才能端到端验证，"
Write-Host "      但读能通就说明特权链和 API 都没问题，剩下的只是有没有目标。"
Write-Host ""
Write-Host "  下一步（部署时，不是现在）：" -ForegroundColor DarkGray
Write-Host "    1. 备份上面的 BootOrder 原始字节" -ForegroundColor DarkGray
Write-Host "    2. 用 bcdedit /copy 或直接写 Boot#### 新增 KLA 项" -ForegroundColor DarkGray
Write-Host "    3. 设 BootNext 指向它，重启验证能进 GRUB" -ForegroundColor DarkGray
Write-Host "    4. 验证通过后才改 BootOrder（risks.md R2）" -ForegroundColor DarkGray
