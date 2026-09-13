<#
.SYNOPSIS
    把官方 mingw 交叉编的 wimlib 二进制部署到 %ProgramData%\KLA\bin\。

.DESCRIPTION
    HANDOFF.md §3 第 3 项"wimlib 交叉编译到 Windows"的实际落地：
    wimlib 作者 Eric Biggers 官方就提供 mingw 交叉编好的 Windows 二进制
    （zip 包，含 wimlib-imagex.exe + libwim-15.dll，依赖全部静态链接）。
    本脚本解压/部署这一份，省去自己编 mingw + libxml2 + ntfs-3g 的麻烦。

    版本固定 1.14.4：跟 kla/docs/backup-design.md 第 269 行明确写的
    "chroot 里那份会随 ACCESS 出厂的 wimlib 1.14.4" 完全对齐，
    避免 Windows 侧打的 WIM 在 Linux 侧读不出来的兼容性风险。

    部署后 Backup.cs 的 ExpectedToolPath() 直接命中：
        %ProgramData%\KLA\bin\wimlib-imagex.exe

.NOTES
    要管理员。要重装/升级加 -Force。
    不装 devel\（开发头文件和 .lib）和 doc\（PDF 手册）——运行时用不到。
#>
[CmdletBinding()]
param(
    # 源 zip。默认指向 app/build/wimlib-1.14.4-windows-x86_64-bin.zip
    # （由本工程的下载步骤下到此处）。
    [string]$SourceZip = (Join-Path $PSScriptRoot '..\..\app\build\wimlib-1.14.4-windows-x86_64-bin.zip'),

    # 部署目录。Backup.cs ExpectedToolPath() 写死 %ProgramData%\KLA\bin\。
    [string]$DestDir = (Join-Path $env:ProgramData 'KLA\bin'),

    [switch]$Force
)

$ErrorActionPreference = 'Stop'

# ── 1. 管理员检查 ──────────────────────────────────────────────────
# 不像 KlaFirmware 要 SE_SYSTEM_ENVIRONMENT，这里只要 admin 写 %ProgramData%。
$id = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($id)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "要管理员权限（要写 $DestDir）。请在管理员 PowerShell 里跑。"
}

# ── 2. 校验源 zip ──────────────────────────────────────────────────
$SourceZip = [System.IO.Path]::GetFullPath($SourceZip)
if (-not (Test-Path -LiteralPath $SourceZip)) {
    throw "找不到源 zip：$SourceZip。先跑下载步骤把 wimlib-1.14.4-windows-x86_64-bin.zip 下到 app/build/。"
}

# ── 3. 已装检查（幂等） ───────────────────────────────────────────
$destExe = Join-Path $DestDir 'wimlib-imagex.exe'
if ((Test-Path -LiteralPath $destExe) -and -not $Force) {
    $existing = & $destExe --version 2>$null | Select-Object -First 1
    Write-Host "已装：$existing"
    Write-Host "要覆盖加 -Force。"
    return
}

# ── 4. 解压到临时目录 ──────────────────────────────────────────────
$tmp = Join-Path $env:TEMP "kla-wimlib-install-$(Get-Random)"
try {
    Write-Host "解压  <- $SourceZip"
    Expand-Archive -Path $SourceZip -DestinationPath $tmp -Force

    # ── 5. 验证二进制能跑 ──────────────────────────────────────────
    # 提前发现 zip 损坏或架构错（比如下到 i686 版了）
    $tmpExe = Join-Path $tmp 'wimlib-imagex.exe'
    if (-not (Test-Path -LiteralPath $tmpExe)) {
        throw "zip 里没 wimlib-imagex.exe，可能下错包：$SourceZip"
    }
    # 2>$null：wimlib-imagex 把 usage 写到 stderr，PS 会把它当错误流，
    # 这是 PS 怪癖不是 exe 问题（Backup.cs 第 559 行也吐槽过）。
    $versionLine = & $tmpExe --version 2>$null | Select-Object -First 1
    if ($versionLine -notmatch '1\.14\.4') {
        throw "二进制版本不是 1.14.4：'$versionLine'。请用 1.14.4 的 zip 以跟 Linux 侧对齐。"
    }
    Write-Host "版本  : $versionLine"

    # ── 6. 复制到目标目录 ──────────────────────────────────────────
    if (-not (Test-Path -LiteralPath $DestDir)) {
        New-Item -ItemType Directory -Path $DestDir -Force | Out-Null
    }

    # 装的文件清单。不装 devel/（开发头）和 doc/（PDF 手册）。
    $files = @(
        'wimlib-imagex.exe',      # 主程序
        'libwim-15.dll',          # 主库，必需
        # 12 个 .cmd 包装是给命令行用户的便利：wimapply / wimcapture / wiminfo
        # 等短命令，等价于 wimlib-imagex <subcmd>。Backup.cs 不用它们，
        # 但留着方便手工调试和给运维人员用。
        'wimappend.cmd','wimapply.cmd','wimcapture.cmd','wimdir.cmd',
        'wimexport.cmd','wimextract.cmd','wiminfo.cmd','wimjoin.cmd',
        'wimoptimize.cmd','wimsplit.cmd','wimupdate.cmd','wimverify.cmd',
        # 许可证文本必须随附以合规（GPLv3/LGPLv3 要求）
        'COPYING.GPLv3.txt','COPYING.LGPLv3.txt','COPYING.libdivsufsort-lite.txt','COPYING.txt',
        'README.txt','NEWS.txt'
    )
    foreach ($f in $files) {
        $src = Join-Path $tmp $f
        if (-not (Test-Path -LiteralPath $src)) {
            Write-Warning "  zip 里没 $f，跳过"
            continue
        }
        Copy-Item -LiteralPath $src -Destination $DestDir -Force
    }
    Write-Host "目标  : $DestDir"

    # ── 7. 部署后验证 ──────────────────────────────────────────────
    # 用部署后的 exe 再跑一次 --version：确认 dll 找得到、能独立工作。
    $installedLine = & $destExe --version 2>$null | Select-Object -First 1
    if ($installedLine -notmatch '1\.14\.4') {
        throw "部署后验证失败：'$installedLine'"
    }

    # 写部署清单：版本、时间、源 zip 指纹。给 Backup.cs 后续可读，
    # 也可让运维快速看到这台机器装的是哪个 wimlib。
    $sha = (Get-FileHash -LiteralPath $SourceZip -Algorithm SHA256).Hash
    $manifest = [PSCustomObject]@{
        Component    = 'wimlib-imagex'
        Version      = '1.14.4'
        InstalledAt  = (Get-Date).ToString('s')
        SourceZip    = $SourceZip
        SourceSha256 = $sha
        DestDir      = $DestDir
    }
    $manifest | ConvertTo-Json |
        Set-Content -LiteralPath (Join-Path $DestDir 'wimlib-installed.json') -Encoding UTF8

    Write-Host ""
    Write-Host "OK: $installedLine"
    Write-Host "部署清单: $(Join-Path $DestDir 'wimlib-installed.json')"
} finally {
    Remove-Item -LiteralPath $tmp -Recurse -Force -ErrorAction SilentlyContinue
}
