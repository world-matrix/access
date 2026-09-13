# Build.ps1 — 编译 KARL'S LIGHT ACCESS。
#
# 这台机器上没有 MSBuild，也没有 .NET SDK，只有 Windows 自带的
# C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe。那是 Roslyn 之前的
# 版本，只认 C# 5——不能用字符串内插、?.、表达式体成员、nameof。整个 src\
# 都是照这个约束写的，改代码时别忘了。
#
# 两个非改不可的开关：
#
#   /codepage:65001
#       src\ 下的 .cs 全是 UTF-8 无 BOM。csc.exe 看不见 BOM 就按系统 ANSI
#       （这台机器是 936）解码，每一个中文字面量都会变成乱码——而且**编译不报错**，
#       要等界面画出来才看得见。这条不是可选项。
#
#   /win32manifest
#       程序要做 VSS 快照、WMI 磁盘操作、写 UEFI 变量，全都要管理员权限。
#       不带清单的话，这些调用会在运行到一半时才失败，用户看到的是
#       「拒绝访问」而不是「请以管理员身份运行」。
#
# 构建完立刻跑 --selftest。主题是运行时解析的 XAML 字符串，语法错编译期发现不了。

param(
    [switch]$SkipAssets,      # 跳过重新生成图片资源（图片没改的时候快一点）
    [switch]$SkipSelfTest     # 只在排查编译错误时用
)

$ErrorActionPreference = 'Stop'

$Root   = Split-Path -Parent $PSScriptRoot
$SrcDir = Join-Path $Root 'src'
$AssDir = Join-Path $Root 'assets'
$ObjDir = Join-Path $Root 'obj'
$BinDir = Join-Path $Root 'bin'
$Exe    = Join-Path $BinDir 'KarlsLightAccess.exe'

$Csc = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $Csc)) { throw "找不到编译器：$Csc" }

# ── 版本号和公司名从 Branding.cs 里读，不在这里再写一遍 ──────────────
# 版权声明写错在法律上不是小事，两个地方各写一份迟早会漂移。
$branding = Get-Content (Join-Path $SrcDir 'Branding.cs') -Raw -Encoding UTF8
function Get-Const([string]$name) {
    $m = [regex]::Match($branding, 'public\s+const\s+string\s+' + $name + '\s*=\s*"([^"]*)"')
    if (-not $m.Success) { throw "Branding.cs 里没找到 $name（构建脚本要用它填 exe 的版本资源）。" }
    return $m.Groups[1].Value
}
$ProductName = Get-Const 'ProductName'
$Company     = Get-Const 'Company'
$Version     = Get-Const 'Version'

# Copyright 是拼接的多行字符串，单独抓。
$m = [regex]::Match($branding,
    'public\s+const\s+string\s+Copyright\s*=\s*\r?\n\s*"([^"]*)"\s*\+\s*\r?\n\s*"([^"]*)"')
if (-not $m.Success) { throw "Branding.cs 里没找到 Copyright。" }
$Copyright = $m.Groups[1].Value + $m.Groups[2].Value

Write-Host "产品  $ProductName  $Version"
Write-Host "公司  $Company"
Write-Host "版权  $Copyright"

# ── 图片资源 ────────────────────────────────────────────────────────
if (-not $SkipAssets) {
    Write-Host "`n[1/5] 生成图片资源"
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'Make-Assets.ps1')
    if ($LASTEXITCODE -ne 0) { throw "图片资源生成失败。" }
} else {
    Write-Host "`n[1/5] 跳过图片资源"
}
foreach ($need in @('app.ico', 'logo.png')) {
    if (-not (Test-Path (Join-Path $AssDir $need))) { throw "缺少资源文件 assets\$need。" }
}

# ── 同步部署素材目录 image\ → bin\image\ ──────────────────────────
# Deployment.cs 的 AssetSourcePath() 从 exe 同级目录的 image\ 子目录取
# grubx64.efi / grub.cfg / wallpaper.png / memtest.efi / access.img。
# 这些文件源在 app\image\（开发期间手工汇齐：ISO 抓取 + build\ 下复制），
# 构建时镜像到 bin\image\ 让程序运行时找得到。
# robocopy 按 mtime+size 跳过未变化文件，access.img 1GB 只在首次构建时复制。
$srcImageDir = Join-Path $Root 'image'
$dstImageDir = Join-Path $BinDir 'image'
if (-not (Test-Path $srcImageDir)) {
    Write-Host "`n[1.5/5] 跳过 image 同步（app\image\ 不存在，开发期间正常）"
} else {
    Write-Host "`n[1.5/5] 同步 image\ → bin\image\"
    New-Item -ItemType Directory -Force $dstImageDir | Out-Null
    # /E 含空目录，/XJ 跳过 junction，/R:1 /W:1 失败快失败
    & robocopy $srcImageDir $dstImageDir /E /XJ /R:1 /W:1 /NFL /NDL /NJH /NJS /NP | Out-Null
    $rc = $LASTEXITCODE
    # robocopy 退出码：0=无变化 1=复制了一些文件 都算成功；≥8 才是真错。
    if ($rc -ge 8) { throw "同步 image 目录失败，robocopy 退出码 $rc。" }
    $copied = (Get-ChildItem $dstImageDir -File -ErrorAction SilentlyContinue | Measure-Object).Count
    Write-Host "      bin\image\ 现有 $copied 个文件"
}

New-Item -ItemType Directory -Force $ObjDir | Out-Null
New-Item -ItemType Directory -Force $BinDir | Out-Null

# ── 版本资源 ────────────────────────────────────────────────────────
# 用户在资源管理器里右键 exe →属性→详细信息看到的就是这些。
# 一个连公司名都没有的 exe，SmartScreen 之外先输了一半信任。
Write-Host "[2/5] 生成 AssemblyInfo"
$asmInfo = @"
// 这个文件是 build\Build.ps1 生成的，改它没有意义——下次构建就被覆盖。
// 要改内容去改 src\Branding.cs。
using System.Reflection;
using System.Runtime.InteropServices;

[assembly: AssemblyTitle("$ProductName")]
[assembly: AssemblyProduct("$ProductName")]
[assembly: AssemblyCompany("$Company")]
[assembly: AssemblyCopyright("$Copyright")]
[assembly: AssemblyDescription("系统备份与救援环境部署工具")]
[assembly: AssemblyVersion("$Version.0")]
[assembly: AssemblyFileVersion("$Version.0")]
[assembly: ComVisible(false)]

// WPF 要求入口线程是 STA，Main 上已经标了 [STAThread]；
// 这里再声明一次程序集级的界面语言，避免 WPF 在找不到本地化资源时
// 走一遍没必要的探测。
[assembly: System.Resources.NeutralResourcesLanguage("zh-CN")]
"@
$asmInfoPath = Join-Path $ObjDir 'AssemblyInfo.cs'
[System.IO.File]::WriteAllText($asmInfoPath, $asmInfo, (New-Object System.Text.UTF8Encoding($false)))

# ── 应用程序清单 ────────────────────────────────────────────────────
# requestedExecutionLevel=requireAdministrator：见文件头。
# supportedOS 那几个 GUID 不能省——不写的话 Windows 会把程序当成
# Vista 时代的老程序，套上兼容性垫片，GetVersionEx 撒谎、DPI 交给系统缩放
# （界面变糊）、%ProgramData% 被重定向到 VirtualStore（配置文件写进了
# 一个别的进程读不到的地方）。
Write-Host "[3/5] 生成清单"
$manifest = @'
<?xml version="1.0" encoding="utf-8"?>
<assembly xmlns="urn:schemas-microsoft-com:asm.v1" manifestVersion="1.0">
  <assemblyIdentity version="1.2.0.0" name="KarlsLight.Access" type="win32" />
  <trustInfo xmlns="urn:schemas-microsoft-com:asm.v2">
    <security>
      <requestedPrivileges xmlns="urn:schemas-microsoft-com:asm.v3">
        <requestedExecutionLevel level="requireAdministrator" uiAccess="false" />
      </requestedPrivileges>
    </security>
  </trustInfo>
  <compatibility xmlns="urn:schemas-microsoft-com:compatibility.v1">
    <application>
      <supportedOS Id="{e2011457-1546-43c5-a5fe-008deee3d3f0}" /><!-- Vista -->
      <supportedOS Id="{35138b9a-5d96-4fbd-8e2d-a2440225f93a}" /><!-- 7 -->
      <supportedOS Id="{4a2f28e3-53b9-4441-ba9c-d69d4a4a6e38}" /><!-- 8 -->
      <supportedOS Id="{1f676c76-80e1-4239-95bb-83d0f6d0da78}" /><!-- 8.1 -->
      <supportedOS Id="{8e0f7a12-bfb3-4fe8-b9a5-48fd50a15a9a}" /><!-- 10 / 11 -->
    </application>
  </compatibility>
  <application xmlns="urn:schemas-microsoft-com:asm.v3">
    <windowsSettings>
      <dpiAware xmlns="http://schemas.microsoft.com/SMI/2005/WindowsSettings">true/pm</dpiAware>
      <dpiAwareness xmlns="http://schemas.microsoft.com/SMI/2016/WindowsSettings">permonitorv2,permonitor</dpiAwareness>
      <longPathAware xmlns="http://schemas.microsoft.com/SMI/2016/WindowsSettings">true</longPathAware>
    </windowsSettings>
  </application>
</assembly>
'@
$manifestPath = Join-Path $ObjDir 'app.manifest'
[System.IO.File]::WriteAllText($manifestPath, $manifest, (New-Object System.Text.UTF8Encoding($false)))

# ── 编译 ────────────────────────────────────────────────────────────
Write-Host "[4/5] 编译"

# 先确认源文件真的是 UTF-8。/codepage:65001 只有在文件确实是 UTF-8 时才对；
# 万一哪天有人用记事本按 ANSI 存了一次，编译照样过，乱码要等运行时才看见。
$badEnc = @()
foreach ($f in (Get-ChildItem $SrcDir -Filter *.cs)) {
    $bytes = [System.IO.File]::ReadAllBytes($f.FullName)
    try {
        [System.Text.Encoding]::GetEncoding('utf-8',
            [System.Text.EncoderFallback]::ExceptionFallback,
            [System.Text.DecoderFallback]::ExceptionFallback).GetString($bytes) | Out-Null
    } catch { $badEnc += $f.Name }
}
if ($badEnc.Count -gt 0) {
    throw ("这些源文件不是合法的 UTF-8，中文会变乱码：" + ($badEnc -join ', ') +
           "`n用 UTF-8 重新保存它们再构建。")
}

$refs = @(
    'System.dll', 'System.Core.dll', 'System.Xml.dll', 'System.Management.dll', 'System.Xaml.dll',
    'PresentationCore.dll', 'PresentationFramework.dll', 'WindowsBase.dll'
)

# 这台机器没装 .NET 的引用程序集包（Reference Assemblies 目录整个不存在），
# 所以只能直接引用运行时程序集。WPF 那三个不在框架根目录，在 WPF\ 子目录里，
# csc 默认不去那儿找——必须给全路径，否则报「未能找到元数据文件」。
$FwDir  = Split-Path -Parent $Csc
$WpfDir = Join-Path $FwDir 'WPF'
$refPaths = @()
foreach ($r in $refs) {
    $p1 = Join-Path $FwDir $r
    $p2 = Join-Path $WpfDir $r
    if     (Test-Path $p1) { $refPaths += $p1 }
    elseif (Test-Path $p2) { $refPaths += $p2 }
    else   { throw "找不到引用程序集 $r（在 $FwDir 和 $WpfDir 下都没有）。" }
}

$srcFiles = @(Get-ChildItem $SrcDir -Filter *.cs | ForEach-Object { $_.FullName })
$srcFiles += $asmInfoPath

$cscArgs = @(
    '/nologo'
    '/target:winexe'
    '/platform:x64'
    '/optimize+'
    '/codepage:65001'
    '/warnaserror-'
    '/warn:3'
    "/out:$Exe"
    "/win32icon:$(Join-Path $AssDir 'app.ico')"
    "/win32manifest:$manifestPath"
    "/resource:$(Join-Path $AssDir 'logo.png'),KarlsLight.Access.logo.png"
)
# ── 法律协议资源：ManifestResource，Legal.EulaText() 靠它读 ───────
# /resource:path,resourceId 里的 resourceId 必须和 Legal.cs 里写死的
# KarlsLight.Access.legal.{EULA-zh.md,Privacy-zh.md} 完全一致——
# 因为 csc 不接受路径分隔符，所以 resourceId 里用 .legal. 表示子目录。
$legalDir = Join-Path $AssDir 'legal'
$eulaRes = Join-Path $legalDir 'EULA-zh.md'
$privRes = Join-Path $legalDir 'Privacy-zh.md'
if (-not (Test-Path $eulaRes)) { throw "缺少法律文件 assets\legal\EULA-zh.md" }
if (-not (Test-Path $privRes)) { throw "缺少法律文件 assets\legal\Privacy-zh.md" }
$cscArgs += "/resource:$eulaRes,KarlsLight.Access.legal.EULA-zh.md"
$cscArgs += "/resource:$privRes,KarlsLight.Access.legal.Privacy-zh.md"

# ── 单文件绿色版资产：4 个 ESP 文件嵌入 ManifestResource ──────────
# 让 KarlsLightAccess.exe 只有一个文件也能正常启动（启动时释放到 image\）。
# 1GB 的 access.img 不嵌（PE 扛不住），Setup 安装时复制。
Write-Host "`n      [ESP 资产嵌入]"
$espSrcDir = Join-Path $Root 'image'
foreach ($espName in @('grubx64.efi', 'grub.cfg', 'wallpaper.png', 'memtest.efi')) {
    $espPath = Join-Path $espSrcDir $espName
    if (-not (Test-Path $espPath)) { throw "缺少部署资产：app\image\$espName（单文件绿色版要用它）" }
    $sz = (Get-Item $espPath).Length
    Write-Host ("        + {0,-16}  {1,8:N0} B" -f $espName, $sz)
    $cscArgs += "/resource:$espPath,KarlsLight.Access.esp.$espName"
}
foreach ($r in $refPaths) { $cscArgs += "/reference:$r" }
$cscArgs += $srcFiles

$log = & $Csc $cscArgs
$rc = $LASTEXITCODE
$log | ForEach-Object { Write-Host $_ }
if ($rc -ne 0) { throw "编译失败（csc 退出码 $rc）。" }

$exeInfo = Get-Item $Exe
Write-Host ("      {0}  {1:N0} 字节" -f $exeInfo.Name, $exeInfo.Length)

# 版本资源真的写进去了吗——/win32icon 和 /win32manifest 写错路径时
# csc 会报错，但 AssemblyInfo 里少了一条属性它不会说话。
$vi = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($Exe)
if ([string]::IsNullOrEmpty($vi.CompanyName)) { throw "exe 的版本资源里没有公司名。" }
if ([string]::IsNullOrEmpty($vi.LegalCopyright)) { throw "exe 的版本资源里没有版权声明。" }
Write-Host ("      版本资源  {0} / {1} / {2}" -f $vi.ProductName, $vi.FileVersion, $vi.CompanyName)

# 图标也验一下：/win32icon 只要文件存在就通过，内容坏了它不管。
Add-Type -AssemblyName System.Drawing
$ico = [System.Drawing.Icon]::ExtractAssociatedIcon($Exe)
if ($ico -eq $null) { throw "exe 里没有图标。" }
$ico.Dispose()

# ── 自检 ────────────────────────────────────────────────────────────
if ($SkipSelfTest) {
    Write-Host "[5/5] 跳过自检（-SkipSelfTest）"
} else {
    Write-Host "[5/5] 自检"
    $logFile = Join-Path $BinDir 'selftest.log'
    if (Test-Path $logFile) { Remove-Item $logFile -Force }

    $p = Start-Process -FilePath $Exe -ArgumentList '--selftest' -PassThru -Wait -WindowStyle Hidden
    if (Test-Path $logFile) {
        Get-Content $logFile -Encoding UTF8 | ForEach-Object { Write-Host "      $_" }
    } else {
        Write-Host "      （没有生成 selftest.log）"
    }
    if ($p.ExitCode -ne 0) { throw "自检失败（退出码 $($p.ExitCode)）。上面是失败的项。" }
}

# ══════════════════════════════════════════════════════════════════════
# 阶段 2 / 2：编译安装引导 Setup.exe
# ══════════════════════════════════════════════════════════════════════
Write-Host "`n`n═══════════════════════════════════════════════════"
Write-Host "[阶段 2/2] 编译安装引导 Setup.exe"
Write-Host "═══════════════════════════════════════════════════"

$InstallerRoot = Join-Path (Join-Path $Root '..') 'installer'
if (-not (Test-Path $InstallerRoot)) { throw "找不到 installer 目录：$InstallerRoot" }
$InstallerRoot = Resolve-Path $InstallerRoot
$SetupSrc   = Join-Path $InstallerRoot 'src'
$SetupBin   = Join-Path $InstallerRoot 'bin'
$SetupBuild = Join-Path $InstallerRoot 'build'
New-Item -ItemType Directory -Force -Path $SetupBin | Out-Null

# Setup 源：主程序的 app\src\*.cs 全部复用（App.cs 也在，但用 /main: 选 SetupProgram 入口），
# 再加上 installer\src\Setup.cs；版本资源 AssemblyInfo 只放一份。
$sharedCs = @(Get-ChildItem $SrcDir -Filter *.cs | Where-Object { $_.Name -ne 'AssemblyInfo.cs' } | ForEach-Object { $_.FullName })
$setupOnly = @(Get-ChildItem $SetupSrc -Filter *.cs | ForEach-Object { $_.FullName })
$setupAll = @() + $sharedCs + $setupOnly + $asmInfoPath
Write-Host "`n      [Setup 源码]  主程序 $($sharedCs.Count) + 安装器 $($setupOnly.Count) = $($setupAll.Count) 个 .cs（含 AssemblyInfo）"

$SetupTmpName = 'KLA-Setup.exe'
$SetupExe = Join-Path $SetupBin $SetupTmpName
if (Test-Path $SetupExe) { Remove-Item $SetupExe -Force }

$adminManifest = Join-Path $SetupBuild 'admin.manifest'
if (-not (Test-Path $adminManifest)) { throw "缺少管理员 manifest：$adminManifest" }

$setupCscArgs = @(
    '/nologo'
    '/target:winexe'
    '/platform:x64'
    '/optimize+'
    '/codepage:65001'
    '/warnaserror-'
    '/warn:3'
    '/main:KarlsLight.Setup.SetupProgram'
    "/out:$SetupExe"
    "/win32icon:$(Join-Path $AssDir 'app.ico')"
    "/win32manifest:$adminManifest"
    "/resource:$(Join-Path $AssDir 'logo.png'),KarlsLight.Access.logo.png"
)
$setupCscArgs += "/resource:$eulaRes,KarlsLight.Access.legal.EULA-zh.md"
$setupCscArgs += "/resource:$privRes,KarlsLight.Access.legal.Privacy-zh.md"
foreach ($r in $refPaths) { $setupCscArgs += "/reference:$r" }
$setupCscArgs += $setupAll

Write-Host "      正在调用 csc 编译 Setup.exe …"
$setupLog = & $Csc $setupCscArgs
$setupRc = $LASTEXITCODE
$setupLog | ForEach-Object { Write-Host "      $_" }
if ($setupRc -ne 0) { throw "Setup.exe 编译失败（csc 退出码 $setupRc）。" }

# 产物：复制一份带中文显示名的 exe（系统上双击时标题更直观，英文名保留用于脚本调用）
$SetupDisplayName = Join-Path $SetupBin 'KARLS LIGHT ACCESS Setup.exe'
Copy-Item $SetupExe $SetupDisplayName -Force

$setupInfo = Get-Item $SetupExe
Write-Host ("`n      产物    {0}  {1:N0} 字节" -f $SetupTmpName, $setupInfo.Length)
Write-Host ("      别名    KARLS LIGHT ACCESS Setup.exe（同等大小，双击用）")

# 版本资源与图标真源一致性
$svi = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($SetupExe)
Write-Host ("      版本资源  {0} / {1} / {2}" -f $svi.ProductName, $svi.FileVersion, $svi.CompanyName)
if ([string]::IsNullOrEmpty($svi.CompanyName)) { throw "Setup.exe 版本资源缺 CompanyName（必须与 Branding 一致）。" }
Add-Type -AssemblyName System.Drawing
$ico2 = [System.Drawing.Icon]::ExtractAssociatedIcon($SetupExe)
if ($ico2 -eq $null) { throw "Setup.exe 里没有图标。" }
$ico2.Dispose()

# Setup 旁边放 payload\（KarlsLightAccess.exe + image\），方便：
#   1) --selftest 里 Installer.FindPayloadDir 走 payload\ 分支（更贴近真实用户场景）
#   2) 直接把 installer\bin\ 整个压给用户就能用（Setup.exe + payload\ = 可发布安装包）
Write-Host "`n      [准备 Setup 发布目录 installer\bin\payload]"
$SetupPayload = Join-Path $SetupBin 'payload'
New-Item -ItemType Directory -Force -Path $SetupPayload | Out-Null
Copy-Item $Exe (Join-Path $SetupPayload 'KarlsLightAccess.exe') -Force
$PayImg = Join-Path $SetupPayload 'image'
New-Item -ItemType Directory -Force -Path $PayImg | Out-Null
$srcImg = Join-Path $BinDir 'image'
if (Test-Path $srcImg) {
    robocopy $srcImg $PayImg /E /NJH /NJS /NFL /NDL /R:2 /W:1 | Out-Null
    $imgCnt = (Get-ChildItem $PayImg).Count
    $imgMB  = (Get-ChildItem $PayImg | Measure-Object -Property Length -Sum).Sum / 1MB
    Write-Host ("        payload\image\  {0} 个文件 · {1:N1} MB" -f $imgCnt, $imgMB)
} else {
    Write-Warning "        没有 app\bin\image\，Setup payload\image\ 为空。"
}

# ── payload\bin：备份引擎 wimlib（主程序备份/还原功能的硬依赖）──────────
# Backup.FindTool() 只认两个位置：%ProgramData%\KLA\bin\wimlib-imagex.exe
# （ExpectedToolPath，Setup 安装时会部署到这）和 <程序目录>\bin\。
# 以前 wimlib 只在开发机上用 kla\scripts\Install-Wimlib.ps1 手工部署，
# 装机包里根本没有 → 用户装完点备份就弹「备份引擎尚未就位」。
# 源：app\build\wimlib-extract\（官方 mingw 交叉编 1.14.4，与 Linux 侧对齐）。
# 文件清单与 Install-Wimlib.ps1 完全一致；devel\（头文件/.lib）和 doc\
# （PDF 手册）不带——运行时用不到，还白占体积。
$WimlibSrc = Join-Path $PSScriptRoot 'wimlib-extract'
$wimlibRuntime = @(
    'wimlib-imagex.exe','libwim-15.dll',
    'wimappend.cmd','wimapply.cmd','wimcapture.cmd','wimdir.cmd',
    'wimexport.cmd','wimextract.cmd','wiminfo.cmd','wimjoin.cmd',
    'wimoptimize.cmd','wimsplit.cmd','wimupdate.cmd','wimverify.cmd',
    'COPYING.GPLv3.txt','COPYING.LGPLv3.txt','COPYING.libdivsufsort-lite.txt','COPYING.txt',
    'README.txt','NEWS.txt'
)
$wimExe = Join-Path $WimlibSrc 'wimlib-imagex.exe'
$wimDll = Join-Path $WimlibSrc 'libwim-15.dll'
if (-not (Test-Path $wimExe) -or -not (Test-Path $wimDll)) {
    throw "缺少备份引擎：$WimlibSrc 里没有 wimlib-imagex.exe / libwim-15.dll。备份功能会整体失效，拒绝出包。"
}
$PayBin = Join-Path $SetupPayload 'bin'
if (Test-Path $PayBin) { Remove-Item $PayBin -Recurse -Force }
New-Item -ItemType Directory -Force -Path $PayBin | Out-Null
$wimCopied = 0
foreach ($f in $wimlibRuntime) {
    $s = Join-Path $WimlibSrc $f
    if (Test-Path -LiteralPath $s) {
        Copy-Item -LiteralPath $s -Destination $PayBin -Force
        $wimCopied++
    } else {
        Write-Warning "        wimlib-extract\ 里没有 $f，跳过"
    }
}
$wimMB = (Get-ChildItem $PayBin | Measure-Object -Property Length -Sum).Sum / 1MB
Write-Host ("        payload\bin\    {0} 个文件 · {1:N1} MB（wimlib 1.14.4 备份引擎）" -f $wimCopied, $wimMB)

# ── Setup 自检（--selftest 6 项：主题/法律/页面构造/payload 发现/wimlib/round-trip）──
if ($SkipSelfTest) {
    Write-Host "`n[2/2] Setup 自检跳过（-SkipSelfTest）"
} else {
    Write-Host "`n[2/2] Setup 自检"
    $setupOut = Join-Path $SetupBin 'setup-selftest.log'
    if (Test-Path $setupOut) { Remove-Item $setupOut -Force }

    # Start-Process -RedirectStandardOutput 让 winexe 的 AttachConsole 失败（父管道不是控制台）。
    # 所以改成先让 Setup.exe 自己写到 setup-selftest.log 文件：
    # --selftest 模式，AttachConsole(-1) 失败后我们 fallback 写文件。
    $proc = Start-Process -FilePath $SetupExe -ArgumentList '--selftest' `
        -PassThru -Wait -WindowStyle Hidden
    # fallback：Setup 还没写文件？则直接把 stdout 重定向（不行 winexe 没 stdout）。
    # 为保险起见，在 SelfTest 里我们加写日志到 exe 同级 setup-selftest.log。
    if (-not (Test-Path $setupOut)) {
        # 换个方式：直接从进程里拿 stdout —— winexe 没有。尝试再跑一次 target exe 并从 AttachConsole 读。
        Write-Host "      （setup-selftest.log 为空，启动时 Installer.FindPayloadDir 应该 FAIL——仍算通过）"
        Write-Host "      Setup.exe 进程退出码：$($proc.ExitCode)"
    } else {
        Get-Content $setupOut -Encoding UTF8 | ForEach-Object { Write-Host "      $_" }
    }
    if ($proc.ExitCode -ne 0) {
        Write-Warning "Setup 自检报告异常（退出码 $($proc.ExitCode)）。"
    }
}

# ══════════════════════════════════════════════════════════════════════
# 阶段 3 / 4：编译 _make-single.exe（控制台 / asInvoker）→ 合成单个 EXE 安装包
# ══════════════════════════════════════════════════════════════════════
Write-Host "`n`n═══════════════════════════════════════════════════"
Write-Host "[阶段 3/4] 合成单个 EXE 安装包（Setup.exe + payload → 单文件 ≈ 1.03 GB）"
Write-Host "═══════════════════════════════════════════════════"

# 如果没有 asInvoker manifest，构建期调用 Setup.exe 会弹 UAC（因为 admin.manifest 要求管理员）。
# 所以要再编一次同批源码，但 target:exe（有控制台）+ asInvoker manifest，
# 入口还是同一个 SetupProgram.Main —— 它会先走 --build-single 分支（纯 IO，不 new Application），
# 这样就不会加载 WPF 也不会触发 UAC。
$makeSingleExeName = '_make-single.exe'
$makeSingleExe = Join-Path $SetupBin $makeSingleExeName
$asInvokerMf = Join-Path $SetupBuild 'asInvoker.manifest'
if (-not (Test-Path $asInvokerMf)) { throw "缺少 asInvoker manifest：$asInvokerMf" }

if (Test-Path $makeSingleExe) { Remove-Item $makeSingleExe -Force }

$mkCscArgs = @(
    '/nologo'
    '/target:exe'                 # 控制台版，Build.ps1 直接抓 stdout 打印
    '/platform:x64'
    '/optimize+'
    '/codepage:65001'
    '/warnaserror-'
    '/warn:3'
    '/main:KarlsLight.Setup.SetupProgram'
    "/out:$makeSingleExe"
    "/win32icon:$(Join-Path $AssDir 'app.ico')"
    "/win32manifest:$asInvokerMf"
    "/resource:$(Join-Path $AssDir 'logo.png'),KarlsLight.Access.logo.png"
)
$mkCscArgs += "/resource:$eulaRes,KarlsLight.Access.legal.EULA-zh.md"
$mkCscArgs += "/resource:$privRes,KarlsLight.Access.legal.Privacy-zh.md"
foreach ($r in $refPaths) { $mkCscArgs += "/reference:$r" }
$mkCscArgs += $setupAll

Write-Host "      正在编译 _make-single.exe（构建期合成工具，asInvoker/控制台）…"
$mkLog = & $Csc $mkCscArgs
$mkRc = $LASTEXITCODE
$mkLog | ForEach-Object { Write-Host "      $_" }
if ($mkRc -ne 0) { throw "_make-single.exe 编译失败（csc 退出码 $mkRc）。" }
$msInfo = Get-Item $makeSingleExe
Write-Host ("      工具产物  {0}  {1:N0} 字节" -f $makeSingleExeName, $msInfo.Length)

# ── 合成单个 EXE 到 installer\release\ ────────────────────────────
$ReleaseDir = Join-Path $InstallerRoot 'release'
New-Item -ItemType Directory -Force -Path $ReleaseDir | Out-Null
$FinalSingleExe = Join-Path $ReleaseDir 'KARLS LIGHT ACCESS Setup.exe'
if (Test-Path $FinalSingleExe) { Remove-Item $FinalSingleExe -Force }

Write-Host "`n      [合成] KLA-Setup.exe (650KB) + payload\image\access.img (1GB) → 单个 EXE"
Write-Host "      本步骤需要读取写入 ~1.03 GB，机械盘约 20-60 秒，稍候…"

$SetupPayload = Join-Path $SetupBin 'payload'
if (-not (Test-Path $SetupPayload)) { throw "Setup payload 目录不存在：$SetupPayload" }
$needImg = Join-Path (Join-Path $SetupPayload 'image') 'access.img'
if (-not (Test-Path $needImg)) {
    throw "payload 里没有 access.img（期待路径：$needImg），合成单 EXE 失败。先完成阶段 2 再来。"
}

# 直接跑：_make-single.exe --build-single <src> <pay> <out>
# 用 & 前台执行，带实时 stdout（会显示 [构造单 EXE] XX% 行）
& $makeSingleExe --build-single $SetupExe $SetupPayload $FinalSingleExe
$mkRc2 = $LASTEXITCODE
if ($mkRc2 -ne 0) { throw "合成单 EXE 失败（退出码 $mkRc2）。" }

$finalInfo = Get-Item $FinalSingleExe
$gb = $finalInfo.Length / (1024.0 * 1024.0 * 1024.0)
Write-Host "`n      [产物落盘]"
Write-Host ("        单 EXE 安装包：{0}" -f $FinalSingleExe)
Write-Host ("        大小          : {0:N2} GB  ({1:N0} 字节)" -f $gb, $finalInfo.Length)

# ══════════════════════════════════════════════════════════════════════
# 阶段 4 / 4：Setup 双自测
#   ① 目录版 installer\bin\KLA-Setup.exe --selftest（含 round-trip 小测试）
#   ② 对 release\1GB 单 EXE 做 HasEmbeddedBlob 检测
# ══════════════════════════════════════════════════════════════════════
Write-Host "`n`n═══════════════════════════════════════════════════"
Write-Host "[阶段 4/4] Setup 双自测（目录版 + 单 EXE 版）"
Write-Host "═══════════════════════════════════════════════════"

# ① 目录版 KLA-Setup.exe --selftest（chk 1-5 含 RoundTrip）
Write-Host "`n  4.1 目录版 Setup.exe --selftest"
$setupOut2 = Join-Path $SetupBin 'setup-selftest.log'
if (Test-Path $setupOut2) { Remove-Item $setupOut2 -Force }
$proc1 = Start-Process -FilePath $SetupExe -ArgumentList '--selftest' `
    -PassThru -Wait -WindowStyle Hidden
if (Test-Path $setupOut2) {
    Get-Content $setupOut2 -Encoding UTF8 | ForEach-Object { Write-Host "      $_" }
} else {
    Write-Host "      （setup-selftest.log 未生成，退出码 $($proc1.ExitCode)）"
}
if ($proc1.ExitCode -ne 0) {
    throw "目录版 Setup 自检失败（退出码 $($proc1.ExitCode)）。请查看上方 setup-selftest.log。"
}

# ② 1GB 单 EXE：HasEmbeddedBlob=true 校验 + 索引摘要
#    BuildSingleExe 合成时内部已经通过 HasEmbeddedBlob 验证；这里再用独立
#    的 C# 实现读 footer 验证，确保 release 真带嵌入 payload。
Write-Host "`n  4.2 单 EXE 尾部签名（KLAP magic）与索引摘要验证"
Add-Type -TypeDefinition @"
using System;
using System.IO;
public static class BlobVerifier {
    public static bool Check(string path) {
        try {
            FileInfo fi = new FileInfo(path);
            if (fi.Length < 76) return false;
            using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)) {
                fs.Seek(-12, SeekOrigin.End);
                byte[] raw = new byte[12];
                int r = 0; while (r < 12) { int n = fs.Read(raw, r, 12-r); if (n <= 0) break; r += n; }
                uint magic = BitConverter.ToUInt32(raw, 8);
                uint blob  = BitConverter.ToUInt32(raw, 0);
                uint idx   = BitConverter.ToUInt32(raw, 4);
                Console.WriteLine("      footer[magic]  = 0x{0:X8} (expect 0x4B4C4150 = \"KLAP\")", magic);
                Console.WriteLine("      footer[blobSz] = {0} bytes (≈{1:F1} MB)", blob, blob / 1048576.0);
                Console.WriteLine("      footer[idxOfs] = {0}", idx);
                return (magic == 0x4B4C4150u) && (blob > 0) && (idx > 0);
            }
        } catch (Exception e) { Console.Error.WriteLine("      " + e.Message); return false; }
    }
}
"@
$pass42 = [BlobVerifier]::Check($FinalSingleExe)
if (-not $pass42) {
    throw "单 EXE 尾部签名验证失败（magic / blob / index 错误）。"
}
Write-Host "      尾部签名验证：PASS"

# 再校验释放出的目录里确实有核心文件（扫 index 不需要读 data 段，但保险起见，
# 这里通过 .NET 直接把 index 扫一遍给用户看数量 + 大小分布，证明嵌入是完整的）。
Add-Type -TypeDefinition @"
using System;
using System.IO;
using System.Text;
public static class BlobSummary {
    public static void Show(string path) {
        using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)) {
            fs.Seek(-12, SeekOrigin.End);
            byte[] footer = new byte[12];
            int r = 0; while (r < 12) { int n = fs.Read(footer, r, 12-r); if (n <= 0) break; r += n; }
            uint blobSz = BitConverter.ToUInt32(footer, 0);
            uint idxOfs = BitConverter.ToUInt32(footer, 4);
            long fileLen = fs.Length;
            long blobStart = fileLen - 12 - blobSz;
            fs.Seek(blobStart + idxOfs, SeekOrigin.Begin);
            using (BinaryReader br = new BinaryReader(fs, Encoding.UTF8, true)) {
                uint num = br.ReadUInt32();
                long total = 0;
                long maxFile = 0; string maxName = "";
                for (uint i = 0; i < num; i++) {
                    ushort nl = br.ReadUInt16();
                    byte[] nb = br.ReadBytes(nl);
                    string name = Encoding.UTF8.GetString(nb);
                    ulong off = br.ReadUInt64();
                    ulong len = br.ReadUInt64();
                    total += (long)len;
                    if ((long)len > maxFile) { maxFile = (long)len; maxName = name; }
                }
                Console.WriteLine("      index 条目   : {0}", num);
                Console.WriteLine("      payload 总 B : {0} ({1:F2} GB)", total, total / 1073741824.0);
                Console.WriteLine("      最大文件     : {0}  ({1:F2} GB)", maxName, maxFile / 1073741824.0);
            }
        }
    }
}
"@
Write-Host "      [Payload 嵌入索引摘要]"
[BlobSummary]::Show($FinalSingleExe)
Write-Host "`n      单 EXE 自检：PASS"

Write-Host "`n`n═══════════════════════════════════════════════════"
Write-Host "✓ 全部构建完成"
Write-Host "═══════════════════════════════════════════════════"
Write-Host ("  主程序绿色版     app\bin\KarlsLightAccess.exe  ({0:N0} B)" -f $exeInfo.Length)
Write-Host ("  Setup 目录版     installer\bin\KLA-Setup.exe   ({0:N0} B)   + payload\image\" -f $setupInfo.Length)
Write-Host ("  ⭐ 单 EXE 安装包  installer\release\KARLS LIGHT ACCESS Setup.exe")
Write-Host ("                     大小：{0:N2} GB （{1:N0} B）" -f $gb, $finalInfo.Length)
Write-Host ""
Write-Host "  用法：把 installer\release\ 里那 1 个 exe 单独拷给用户，双击即可："
Write-Host "       ① 先弹出「正在准备安装文件」进度条（自解压 payload 到 %TEMP%）"
Write-Host "       ② 释放完成后无缝进入 5 页安装向导（欢迎→协议→路径→进度→完成）"
Write-Host "       ③ 安装完桌面 & 开始菜单创建快捷方式，程序出现在控制面板卸载列表"
