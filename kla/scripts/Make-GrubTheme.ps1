<#
.SYNOPSIS
    把品牌壁纸转成 GRUB 能用的 PNG，输出到 app/build/wallpaper.png。

.DESCRIPTION
    给 GRUB 启动菜单用。源图是给印刷/设计用的高分辨率 JPEG，
    这里降采样到 1920 宽并转 PNG：

      - PNG 而不是 JPEG：GRUB 2 EFI 默认带 png 模块，jpg 模块不一定在。
        背景图又是高对比边缘（黑底 + 白字标题），JPEG 振铃会很难看。
      - 1920 宽：和前端 www/assets/wallpaper.jpg 一致，足够覆盖大多数
        笔记本屏幕。GRUB background_image 在屏幕分辨率 < 图像分辨率时会
        居中显示；屏幕分辨率 > 图像分辨率时不放大，留黑边——这两种情况
        都比"被拉伸变形"好看。

.NOTES
    部署：拷到 ESP 的 EFI\KLA\wallpaper.png（grub.cfg 第 24 行引用）。
    和 grub.cfg 同目录便于部署脚本一起拷。

    源图本身上下有黑边印着标题和版权，所以这里只缩放、不裁切、不补 logo。
#>
[CmdletBinding()]
param(
    [string]$Source = 'D:\work\公司\公司\LOGO\access',
    [string]$Dest   = (Join-Path $PSScriptRoot '..\..\app\build\wallpaper.png'),
    [int]$Width     = 1920
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$Dest = [System.IO.Path]::GetFullPath($Dest)
if (-not (Test-Path $Source)) { throw "找不到美术资源目录：$Source" }
$destDir = Split-Path $Dest -Parent
if (-not (Test-Path $destDir)) {
    New-Item -ItemType Directory -Path $destDir -Force | Out-Null
}

# 优先 16:10 那张：救援环境保底分辨率 1024x768、常规 1920x1080，
# 16:10 夹在中间，contain 到任一边留的黑边都最少。
$wallSrc = @(
    (Join-Path $Source 'access wallpaper 1610.jpg'),
    (Join-Path $Source 'access wallpaper.jpg')
) | Where-Object { Test-Path $_ } | Select-Object -First 1

if (-not $wallSrc) {
    throw "源目录里没找到 'access wallpaper 1610.jpg' 或 'access wallpaper.jpg'：$Source"
}

Write-Host "GRUB 壁纸  <- $(Split-Path $wallSrc -Leaf)"

$img = [System.Drawing.Bitmap]::FromFile($wallSrc)
try {
    $h = [int][math]::Round($img.Height * $Width / $img.Width)

    # 高质量重采样。三个 Mode 都要设：只设 InterpolationMode 的话边缘
    # 仍然会有半像素偏移。
    $bmp = New-Object System.Drawing.Bitmap($Width, $h, [System.Drawing.Imaging.PixelFormat]::Format24bppRgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    try {
        $g.CompositingMode    = [System.Drawing.Drawing2D.CompositingMode]::SourceCopy
        $g.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
        $g.InterpolationMode  = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $g.PixelOffsetMode    = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $g.SmoothingMode      = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
        $g.Clear([System.Drawing.Color]::Black)
        $g.DrawImage($img, (New-Object System.Drawing.Rectangle(0, 0, $Width, $h)))
    } finally { $g.Dispose() }

    # PNG 不需要质量参数。Format24bppRgb 比 32bppArgb 小一半——壁纸
    # 不需要 alpha 通道，背景色已经用黑色填满。
    $bmp.Save($Dest, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()

    $a = (Get-Item $wallSrc).Length
    $b = (Get-Item $Dest).Length
    Write-Host ("  {0,8:N0} KB  ->  {1,7:N0} KB   {2}x{3} -> {4}x{5}" -f `
        ($a / 1KB), ($b / 1KB), $img.Width, $img.Height, $Width, $h)
} finally { $img.Dispose() }
