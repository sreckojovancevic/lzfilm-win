<#
.SYNOPSIS
    Builds LzFilm: liblzma (if missing) -> self-extractor stub -> desktop app -> tests.

.EXAMPLE
    .\build.ps1                 # everything
    .\build.ps1 -SkipTests
    .\build.ps1 -Runtime win-arm64

    Result: artifacts\app\LzFilm.exe  (self-contained, no .NET installation needed)
#>
param(
    [string]$Runtime = "win-x64",
    [switch]$SkipTests
)

$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

function Step($text) { Write-Host "`n=== $text" -ForegroundColor Cyan }
function Check($what) { if ($LASTEXITCODE -ne 0) { throw "$what failed (exit code $LASTEXITCODE)." } }

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw "The .NET 8 SDK is required: winget install Microsoft.DotNet.SDK.8"
}

if (-not (Test-Path "native\liblzma.dll")) {
    Step "liblzma.dll not found in native\ - building it"
    & "$PSScriptRoot\tools\build-liblzma.ps1"
}

Step "Self-extractor stub ($Runtime)"
dotnet publish src\LzFilm.Sfx -c Release -r $Runtime -o artifacts\sfx
Check "Publishing the stub"
$stub = Get-Item "artifacts\sfx\LzFilm.Sfx.exe"
Write-Host ("Stub: {0:N1} MB" -f ($stub.Length / 1MB))

Step "Desktop app ($Runtime, self-contained)"
dotnet publish src\LzFilm.App -c Release -r $Runtime -o artifacts\app
Check "Publishing the app"
$app = Get-Item "artifacts\app\LzFilm.exe"
Write-Host ("App:  {0}  ({1:N1} MB)" -f $app.FullName, ($app.Length / 1MB))

if (-not $SkipTests) {
    Step "Tests (including a real self-extracting archive)"
    $env:LZFILM_SFX_STUB = $stub.FullName
    try {
        dotnet run --project tests\LzFilm.Tests -c Release
        Check "Tests"
    }
    finally {
        Remove-Item Env:\LZFILM_SFX_STUB -ErrorAction SilentlyContinue
    }
}

Step "Done"
Write-Host "Run: $($app.FullName)" -ForegroundColor Green
