<#
.SYNOPSIS
    Compares LzFilm (files mode) with 7-Zip on one folder: size, time, peak memory.

.DESCRIPTION
    Runs 7-Zip "Normal" (-mx5) and "Ultra" (-mx9), then LzFilm with several level / segment-size
    combinations, and finally reports how much of the data is duplicate file content (what
    deduplication could save). Results go to the console and to results.csv in the output folder.
    Each archive is deleted after it was measured, so only one needs free space at a time.

.EXAMPLE
    .\tools\bench-pack.ps1 -Source E:\gitcopy
    .\tools\bench-pack.ps1 -Source E:\gitcopy -Out D:\lzbench -Threads 8 -Quick
#>
param(
    [Parameter(Mandatory = $true)][string]$Source,
    [string]$Out = "C:\Temp\lzbench",
    [int]$Threads = 4,
    [switch]$Quick,          # fewer LzFilm configurations
    [switch]$Keep            # keep the archives
)

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
if (-not (Test-Path $Source)) { throw "Source not found: $Source" }
New-Item -ItemType Directory -Force -Path $Out | Out-Null

# 7-Zip
$7z = @("$env:ProgramFiles\7-Zip\7z.exe", "${env:ProgramFiles(x86)}\7-Zip\7z.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $7z) { $7z = (Get-Command 7z -ErrorAction SilentlyContinue).Source }

# LzFilm test runner (contains the bench modes)
Write-Host "Building the bench runner..." -ForegroundColor Cyan
dotnet build "$root\tests\LzFilm.Tests" -c Release -v q --nologo | Out-Null
if ($LASTEXITCODE -ne 0) { throw "Build failed." }
$runner = Get-ChildItem "$root\tests\LzFilm.Tests\bin\Release" -Recurse -Filter LzFilm.Tests.dll | Select-Object -First 1
if (-not $runner) { throw "LzFilm.Tests.dll not found." }

$srcBytes = (Get-ChildItem $Source -Recurse -File -Force -ErrorAction SilentlyContinue | Measure-Object Length -Sum).Sum
Write-Host ("Source: {0}  ({1:N1} MiB)" -f $Source, ($srcBytes / 1MB))
$results = New-Object System.Collections.Generic.List[object]

function Add-Result($tool, $config, $bytes, $seconds, $peakMiB) {
    $r = [pscustomobject]@{
        Tool    = $tool
        Config  = $config
        MiB     = [math]::Round($bytes / 1MB, 1)
        Ratio   = "{0:P1}" -f ($bytes / [math]::Max(1, $srcBytes))
        Seconds = [math]::Round($seconds, 1)
        PeakMiB = $peakMiB
    }
    $results.Add($r)
    $r | Format-Table -HideTableHeaders | Out-String | ForEach-Object { Write-Host $_.Trim() }
}

function Run-7z($name, $switches) {
    if (-not $7z) { return }
    $arc = Join-Path $Out "$name.7z"
    Remove-Item $arc -ErrorAction SilentlyContinue
    Write-Host "`n7-Zip $name ($switches)..." -ForegroundColor Cyan
    $p = Start-Process -FilePath $7z -ArgumentList (@("a", "-t7z", "`"$arc`"", "`"$Source\*`"", "-mmt=$Threads", "-bso0", "-bsp0") + $switches) -NoNewWindow -PassThru
    $peak = 0
    $sw = [Diagnostics.Stopwatch]::StartNew()
    while (-not $p.HasExited) {
        try { $p.Refresh(); $peak = [math]::Max($peak, $p.PeakWorkingSet64) } catch { }
        Start-Sleep -Milliseconds 250
    }
    $sw.Stop()
    if ($p.ExitCode -gt 1) { Write-Warning "7-Zip exit code $($p.ExitCode)" }
    Add-Result "7-Zip" "$name $switches" (Get-Item $arc).Length $sw.Elapsed.TotalSeconds ([math]::Round($peak / 1MB))
    if (-not $Keep) { Remove-Item $arc }
}

function Run-LzFilm($preset, $segMiB) {
    $name = "lz_p${preset}_s${segMiB}"
    Get-ChildItem $Out -Filter "$name.*" -ErrorAction SilentlyContinue | Remove-Item
    Write-Host "`nLzFilm level $preset, segment $segMiB MiB, $Threads threads..." -ForegroundColor Cyan
    $line = & dotnet $runner.FullName packbench $preset $segMiB $Threads $Out $name $Source | Where-Object { $_ -like "RESULT;*" }
    if (-not $line) { Write-Warning "LzFilm run failed."; return }
    $f = $line.Split(";")
    # RESULT;name;bytes;seconds;scanSeconds;files;inputBytes;peakMiB;estimatedMiB
    Add-Result "LzFilm" "level $preset, segment $segMiB MiB (est. $($f[8]) MiB)" ([long]$f[2]) ([double]$f[3]) ([long]$f[7])
    if (-not $Keep) { Get-ChildItem $Out -Filter "$name.*" | Remove-Item }
}

Run-7z "normal" @("-mx5")
Run-7z "ultra" @("-mx9")

$configs = if ($Quick) { @(@(6, 32), @(9, 256)) } else { @(@(6, 32), @(6, 128), @(9, 64), @(9, 256), @(9, 1024)) }
foreach ($c in $configs) { Run-LzFilm $c[0] $c[1] }

Write-Host "`nDuplicate content (what deduplication could save):" -ForegroundColor Cyan
& dotnet $runner.FullName dupes $Source | Where-Object { $_ -notlike "liblzma*" }

Write-Host "`n=== Results" -ForegroundColor Green
$results | Format-Table -AutoSize
$csv = Join-Path $Out "results.csv"
$results | Export-Csv -NoTypeInformation -Path $csv
Write-Host "Saved: $csv"
