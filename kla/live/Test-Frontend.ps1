<#
.SYNOPSIS
    ACCESS 前端的静态自检：图标、DOM id、资源文件、CSS 类名。

.DESCRIPTION
    前端这一层没有单元测试能跑——救援环境里没有 node，也不值得为了跑测试
    往 1 GB 的镜像里塞一个 JS 引擎。但前端最常见的坏法恰恰是静态的、
    一查就能查出来的那几种：

      · <use href="icons.svg#foo"> 里的 foo 图标表里根本没有（图标不显示，静默）
      · $('#wNext') 找的 id 在模板里拼错了（返回 null，点击无反应，静默）
      · CSS 里写的 assets/xxx.png 没被 Import-BrandAssets 生成出来（背景空白）
      · class 名两边对不上（样式不生效，但页面照常渲染）

    这四种全都**不会报错**，只会让功能悄悄失灵——在救援场景里，
    一个点了没反应的按钮比一个明确报错的按钮危险得多。

    有 node 的话额外跑一次 node --check 做真正的语法检查。
#>
[CmdletBinding()]
param(
    [string]$Www = (Join-Path $PSScriptRoot 'config\includes.chroot\opt\kla\www')
)

$ErrorActionPreference = 'Stop'
$Www = [System.IO.Path]::GetFullPath($Www)

$script:Fail = 0
$script:Warn = 0
function Pass($m) { Write-Host "  [OK]   $m" }
function Bad($m)  { Write-Host "  [FAIL] $m" -ForegroundColor Red;    $script:Fail++ }
function Note($m) { Write-Host "  [WARN] $m" -ForegroundColor Yellow; $script:Warn++ }

function ReadAll($p) {
    if (-not (Test-Path $p)) { throw "缺文件：$p" }
    return Get-Content -Raw -Encoding UTF8 -Path $p
}

$js   = ReadAll (Join-Path $Www 'app.js')
$html = ReadAll (Join-Path $Www 'index.html')
$css  = ReadAll (Join-Path $Www 'app.css')
$svg  = ReadAll (Join-Path $Www 'assets\icons.svg')

Write-Host "前端目录：$Www"
Write-Host ""

# ── 1. 图标引用 ───────────────────────────────────────────────────
Write-Host "=== 1. 图标引用 ==="
$have = [regex]::Matches($svg, '<symbol\s+id="([^"]+)"') | ForEach-Object { $_.Groups[1].Value }
# 两种引用形式：HTML 里直接写 icons.svg#id，JS 里走 icon('id', cls) 包一层
$want = @()
$want += [regex]::Matches($js + $html, 'icons\.svg#([A-Za-z0-9_-]+)') | ForEach-Object { $_.Groups[1].Value }
$want += [regex]::Matches($js, "icon\(\s*'([A-Za-z0-9_-]+)'") | ForEach-Object { $_.Groups[1].Value }
# 三元里的两个分支：icon(e.isDir ? 'folder' : 'doc', ...)
$want += [regex]::Matches($js, "\?\s*'([A-Za-z0-9_-]+)'\s*:\s*'([A-Za-z0-9_-]+)'\s*,\s*'ico") |
         ForEach-Object { $_.Groups[1].Value; $_.Groups[2].Value }
# 分类/任务表里的 icon: 'xxx' —— 这些是数据驱动的，调用点写的是 icon(t.icon, ...)，
# 光扫字面量调用会把它们全判成"没人用"。误报比漏报更坏：
# 一份充满假警告的报告，看的人第一反应是把整个警告栏无视掉。
$want += [regex]::Matches($js, "icon:\s*'([A-Za-z0-9_-]+)'") | ForEach-Object { $_.Groups[1].Value }
$want = $want | Sort-Object -Unique

$miss = $want | Where-Object { $have -notcontains $_ }
if ($miss) { Bad "引用了图标表里没有的 id：$($miss -join ', ')" }
else { Pass "$($want.Count) 个被引用的图标全部存在" }

$unused = $have | Where-Object { $want -notcontains $_ }
if ($unused) { Note "图标表里有 $($unused.Count) 个没人用：$($unused -join ', ')" }

# ── 2. DOM id ─────────────────────────────────────────────────────
Write-Host ""
Write-Host "=== 2. DOM id ==="
# id 的来源有两处：index.html 的静态结构，和 app.js 模板串里动态拼出来的
$ids = @()
$ids += [regex]::Matches($html, 'id="([A-Za-z0-9_-]+)"') | ForEach-Object { $_.Groups[1].Value }
$ids += [regex]::Matches($js,   'id="([A-Za-z0-9_-]+)"') | ForEach-Object { $_.Groups[1].Value }
$ids = $ids | Sort-Object -Unique

$queried = [regex]::Matches($js, "\`$\('#([A-Za-z0-9_-]+)'\)") | ForEach-Object { $_.Groups[1].Value } |
           Sort-Object -Unique
$ghost = $queried | Where-Object { $ids -notcontains $_ }
if ($ghost) { Bad "查了不存在的 id（会拿到 null）：$($ghost -join ', ')" }
else { Pass "$($queried.Count) 个被查询的 id 都能对上" }

# ── 3. 资源文件 ───────────────────────────────────────────────────
Write-Host ""
Write-Host "=== 3. 资源文件 ==="
$refs = @()
$refs += [regex]::Matches($css,  'url\(\s*"?(assets/[^")]+)"?\s*\)') | ForEach-Object { $_.Groups[1].Value }
$refs += [regex]::Matches($html, 'src="(assets/[^"]+)"')             | ForEach-Object { $_.Groups[1].Value }
$refs = $refs | Sort-Object -Unique
foreach ($r in $refs) {
    $p = Join-Path $Www ($r -replace '/', '\')
    if (Test-Path $p) {
        Pass ("{0}  ({1:N1} KB)" -f $r, ((Get-Item $p).Length / 1KB))
    } else {
        Bad "$r 不存在 —— 跑一遍 scripts\Import-BrandAssets.ps1"
    }
}

# ── 4. CSS 类名 ───────────────────────────────────────────────────
Write-Host ""
Write-Host "=== 4. CSS 类名 ==="
$defined = [regex]::Matches($css, '\.([a-z][a-z0-9_-]*)') | ForEach-Object { $_.Groups[1].Value } |
           Sort-Object -Unique
$used = @()
$used += [regex]::Matches($html, 'class="([^"]+)"') | ForEach-Object { $_.Groups[1].Value }
$used += [regex]::Matches($js,   'class="([^"$]*)') | ForEach-Object { $_.Groups[1].Value }
$used += [regex]::Matches($js, "classList\.(?:add|remove|toggle|contains)\('([a-z0-9_-]+)'\)") |
         ForEach-Object { $_.Groups[1].Value }
$used = $used -split '\s+' | Where-Object { $_ -match '^[a-z][a-z0-9_-]*$' } | Sort-Object -Unique

$orphan = $used | Where-Object { $defined -notcontains $_ }
if ($orphan) { Note "用了但 CSS 里没有规则的 class：$($orphan -join ', ')" }
else { Pass "$($used.Count) 个 class 都有对应样式" }

# ── 5. JS 语法 ────────────────────────────────────────────────────
Write-Host ""
Write-Host "=== 5. JS 语法 ==="
$node = Get-Command node -ErrorAction SilentlyContinue
if ($node) {
    & $node --check (Join-Path $Www 'app.js')
    if ($LASTEXITCODE -eq 0) { Pass "node --check 通过" } else { Bad "node --check 未通过" }
} else {
    # 没有 node 就退而求其次：数括号。这**不是**语法检查，
    # 只能抓到"少了个右括号"这种最粗的错，抓不到语义问题——
    # 所以它报通过不代表 JS 是对的，别把它当语法检查用。
    $stripped = $js -replace '(?m)//.*$', '' -replace '(?s)/\*.*?\*/', ''
    foreach ($pair in @(@('{','}'), @('(',')'), @('[',']'))) {
        $a = ([regex]::Matches($stripped, [regex]::Escape($pair[0]))).Count
        $b = ([regex]::Matches($stripped, [regex]::Escape($pair[1]))).Count
        if ($a -ne $b) { Note "$($pair[0])$($pair[1]) 数量不等：$a vs $b（字符串里的括号也会被数进去，仅供参考）" }
    }
    Note "没装 node，跳过真正的语法检查（上面的括号计数不算数）"
}

Write-Host ""
Write-Host "================================"
Write-Host "  失败 $script:Fail 项，警告 $script:Warn 项"
Write-Host "================================"
if ($script:Fail -gt 0) { exit 1 }
exit 0
