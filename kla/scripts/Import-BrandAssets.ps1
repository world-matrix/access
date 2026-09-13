<#
.SYNOPSIS
    把 KARL'S LIGHT 的美术源文件转成 ACCESS 前端能直接用的资源。

.DESCRIPTION
    源文件是给印刷/设计用的尺寸（壁纸 7382x4614、logo 2500x2436），
    直接塞进救援镜像是浪费——ACCESS 的整个体积预算才 1 GB，而这两个文件
    加起来 3.9 MB，其中 99% 的像素在 62px 高的横幅里根本看不出来。

    这里统一降采样 + 重压缩，输出到 www/assets/。改了美术稿就重跑一遍。

.NOTES
    壁纸按 contain 而不是 cover 使用：原图上下两条黑边里印着标题和版权，
    cover 裁切会把它们切掉。contain 留出的黑边和页面底色是同一个黑，
    视觉上接不出缝——这也正是原设计留黑边的用意。
#>
[CmdletBinding()]
param(
    [string]$Source = 'D:\work\公司\公司\LOGO\access',
    [string]$Dest   = (Join-Path $PSScriptRoot '..\live\config\includes.chroot\opt\kla\www\assets'),
    [int]$WallpaperWidth = 1920,
    [int]$WallpaperQuality = 82,
    [int]$LogoHeight = 192
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$Dest = [System.IO.Path]::GetFullPath($Dest)
if (-not (Test-Path $Source)) { throw "找不到美术资源目录：$Source" }
if (-not (Test-Path $Dest))   { New-Item -ItemType Directory -Path $Dest -Force | Out-Null }

Write-Host "源  : $Source"
Write-Host "目标: $Dest"
Write-Host ""

# ── 高质量重采样 ───────────────────────────────────────────────────
# 默认的 DrawImage 用的是低质量插值，缩到 1/4 会糊成一团。
# 三个 Mode 都要设：只设 InterpolationMode 的话边缘仍然会有半像素偏移。
function Resize-Bitmap {
    param([System.Drawing.Image]$Src, [int]$W, [int]$H)

    $dst = New-Object System.Drawing.Bitmap($W, $H, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($dst)
    try {
        # SourceCopy 而不是 SourceOver：logo 是白色描边 + 透明底，
        # SourceOver 会把透明像素和黑底混色，缩完边缘发灰。
        $g.CompositingMode    = [System.Drawing.Drawing2D.CompositingMode]::SourceCopy
        $g.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
        $g.InterpolationMode  = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $g.PixelOffsetMode    = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $g.SmoothingMode      = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
        $g.Clear([System.Drawing.Color]::Transparent)
        $g.DrawImage($Src, (New-Object System.Drawing.Rectangle(0, 0, $W, $H)))
    } finally { $g.Dispose() }
    return $dst
}

function Save-Jpeg {
    param([System.Drawing.Bitmap]$Bmp, [string]$Path, [int]$Quality)
    $codec = [System.Drawing.Imaging.ImageCodecInfo]::GetImageEncoders() |
             Where-Object { $_.MimeType -eq 'image/jpeg' }
    $ep = New-Object System.Drawing.Imaging.EncoderParameters(1)
    $ep.Param[0] = New-Object System.Drawing.Imaging.EncoderParameter(
        [System.Drawing.Imaging.Encoder]::Quality, [int64]$Quality)
    $Bmp.Save($Path, $codec, $ep)
    $ep.Dispose()
}

function Show-Result {
    param([string]$Label, [string]$SrcPath, [string]$OutPath, [string]$Dims)
    $a = (Get-Item $SrcPath).Length
    $b = (Get-Item $OutPath).Length
    $pct = [math]::Round(100.0 * $b / $a, 1)
    Write-Host ("  {0,-12} {1,8:N0} KB  ->  {2,7:N0} KB  ({3}%)   {4}" -f `
        $Label, ($a / 1KB), ($b / 1KB), $pct, $Dims)
}

# ── 1. 壁纸 ───────────────────────────────────────────────────────
# 优先用 16:10 那张：救援环境的保底分辨率是 1024x768，常规是 1920x1080，
# 16:10 夹在两者中间，contain 到任一边留的黑边都最少。
$wallSrc = @(
    (Join-Path $Source 'access wallpaper 1610.jpg'),
    (Join-Path $Source 'access wallpaper.jpg')
) | Where-Object { Test-Path $_ } | Select-Object -First 1

if (-not $wallSrc) {
    Write-Warning "没找到壁纸，跳过"
} else {
    Write-Host "壁纸  <- $(Split-Path $wallSrc -Leaf)"
    $img = [System.Drawing.Bitmap]::FromFile($wallSrc)
    try {
        $h = [int][math]::Round($img.Height * $WallpaperWidth / $img.Width)
        $out = Join-Path $Dest 'wallpaper.jpg'
        $bmp = Resize-Bitmap -Src $img -W $WallpaperWidth -H $h
        try { Save-Jpeg -Bmp $bmp -Path $out -Quality $WallpaperQuality } finally { $bmp.Dispose() }
        Show-Result 'wallpaper' $wallSrc $out "$($img.Width)x$($img.Height) -> ${WallpaperWidth}x$h"
    } finally { $img.Dispose() }
}

# ── 2. Logo ───────────────────────────────────────────────────────
# 保持 PNG：logo 是纯色描边 + 透明底，JPEG 既没有 alpha，
# 又会在高对比边缘产生振铃。
$logoSrc = Join-Path $Source 'company logo.png'
if (-not (Test-Path $logoSrc)) {
    Write-Warning "没找到 company logo.png，跳过"
} else {
    Write-Host ""
    Write-Host "Logo  <- company logo.png"
    $img = [System.Drawing.Bitmap]::FromFile($logoSrc)
    try {
        $w = [int][math]::Round($img.Width * $LogoHeight / $img.Height)
        $out = Join-Path $Dest 'logo.png'
        $bmp = Resize-Bitmap -Src $img -W $w -H $LogoHeight
        try { $bmp.Save($out, [System.Drawing.Imaging.ImageFormat]::Png) } finally { $bmp.Dispose() }
        Show-Result 'logo' $logoSrc $out "$($img.Width)x$($img.Height) -> ${w}x$LogoHeight"
    } finally { $img.Dispose() }
}

# ── 3. 体积核算 ───────────────────────────────────────────────────
# 这一栏不是装饰。整个 ACCESS 的体积目标是 1 GB，前端资源要是
# 悄悄涨到几 MB，等发现的时候已经在跟内核和驱动抢空间了。
Write-Host ""
$all = Get-ChildItem $Dest -File
$sum = ($all | Measure-Object -Property Length -Sum).Sum
Write-Host "assets/ 合计 $([math]::Round($sum/1KB,1)) KB —— $($all.Count) 个文件"
$all | Sort-Object Length -Descending | ForEach-Object {
    Write-Host ("    {0,-16} {1,7:N1} KB" -f $_.Name, ($_.Length / 1KB))
}
if ($sum -gt 1MB) {
    Write-Warning "前端资源超过 1 MB，检查一下是不是哪个文件没压缩"
}
