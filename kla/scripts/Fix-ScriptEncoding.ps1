<#
.SYNOPSIS
    Re-save PowerShell scripts as UTF-8 with BOM.

.DESCRIPTION
    Windows PowerShell 5.1 decodes a .ps1 file that has no BOM using the
    system ANSI code page -- 936/GBK on a Chinese Windows install -- not
    UTF-8. Every script here carries Chinese comments and messages, so
    5.1 mis-decodes them at parse time. Some of the resulting byte pairs
    map to characters that terminate a string literal early, which is why
    the reported errors ("unexpected token", "incomplete hash literal",
    "missing closing paren") point at lines that are in fact fine.

    Prepending a UTF-8 BOM makes 5.1 decode the file correctly. It is
    harmless under PowerShell 7, which already assumes UTF-8.

    This file is deliberately pure ASCII: it decodes identically under
    either code page, so it still runs when nothing else does. Keep it
    that way -- do not add non-ASCII text here.

.PARAMETER Path
    Root directory to scan recursively. Defaults to the repository root
    (the parent of the folder holding this script).

.EXAMPLE
    .\Fix-ScriptEncoding.ps1
    .\Fix-ScriptEncoding.ps1 -WhatIf
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string] $Path
)

$ErrorActionPreference = 'Stop'

# $PSScriptRoot is not reliably populated inside a param() default under
# Windows PowerShell 5.1, so resolve the script's own folder here instead.
if (-not $Path) {
    $here = $PSScriptRoot
    if (-not $here) { $here = Split-Path -Parent $MyInvocation.MyCommand.Definition }
    if (-not $here) { $here = (Get-Location).Path }
    $parent = Split-Path -Parent $here
    $Path = if ($parent) { $parent } else { $here }
}

if (-not (Test-Path $Path)) { throw "Path not found: $Path" }
$root = (Resolve-Path $Path).Path

# throwOnInvalidBytes = $true, so a file that is NOT valid UTF-8 raises
# instead of silently decoding to replacement characters. That matters:
# a genuinely ANSI-encoded file would be destroyed by re-encoding it.
$strictUtf8 = New-Object System.Text.UTF8Encoding($false, $true)
$utf8Bom    = New-Object System.Text.UTF8Encoding($true)

$fixed = @(); $already = @(); $pureAscii = @(); $skipped = @()

Write-Host ""
Write-Host "==> Scanning $root" -ForegroundColor Cyan

foreach ($f in (Get-ChildItem -Path $root -Filter *.ps1 -Recurse -File)) {
    $rel   = $f.FullName.Substring($root.Length).TrimStart('\')
    $bytes = [System.IO.File]::ReadAllBytes($f.FullName)

    if ($bytes.Length -ge 3 -and
        $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF) {
        $already += $rel
        continue
    }

    # No BOM is only a problem when the file actually contains non-ASCII.
    $hasHighByte = $false
    foreach ($b in $bytes) { if ($b -gt 0x7F) { $hasHighByte = $true; break } }
    if (-not $hasHighByte) { $pureAscii += $rel; continue }

    try {
        $text = $strictUtf8.GetString($bytes)
    } catch {
        # Already ANSI, or some other encoding. Converting would corrupt it.
        $skipped += $rel
        continue
    }

    if ($PSCmdlet.ShouldProcess($f.FullName, 'add UTF-8 BOM')) {
        [System.IO.File]::WriteAllText($f.FullName, $text, $utf8Bom)
    }
    $fixed += $rel
}

function Show-Group {
    param([string]$Title, [string[]]$Items, [string]$Color)
    if (-not $Items -or $Items.Count -eq 0) { return }
    Write-Host ""
    Write-Host ("  {0} ({1})" -f $Title, $Items.Count) -ForegroundColor $Color
    $Items | ForEach-Object { Write-Host "      $_" -ForegroundColor $Color }
}

Show-Group 'FIXED  - BOM added, these were broken'      $fixed     'Green'
Show-Group 'OK     - already had a BOM'                 $already   'DarkGray'
Show-Group 'OK     - pure ASCII, BOM not needed'        $pureAscii 'DarkGray'
Show-Group 'SKIP   - not valid UTF-8, left untouched'   $skipped   'Yellow'

Write-Host ""
if ($fixed.Count -gt 0) {
    Write-Host "  Done. The scripts above will now parse correctly." -ForegroundColor Green
} else {
    Write-Host "  Nothing to change." -ForegroundColor DarkGray
}
if ($skipped.Count -gt 0) {
    Write-Host "  Check the SKIP list by hand -- re-encoding them blindly would corrupt them." -ForegroundColor Yellow
}
Write-Host ""
