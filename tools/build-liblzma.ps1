<#
.SYNOPSIS
    Builds liblzma.dll (xz-utils) from the official sources and copies it to native\liblzma.dll.

.DESCRIPTION
    Needs git and CMake with the Visual Studio C++ tools (CMake that comes with Visual Studio is found
    automatically). The C runtime is linked statically, so the DLL has no dependency on the
    Visual C++ Redistributable.

    Alternatives, if you prefer:
      vcpkg:   vcpkg install liblzma:x64-windows   ->  copy installed\x64-windows\bin\liblzma.dll
      MSYS2:   pacman -S mingw-w64-x86_64-xz        ->  copy mingw64\bin\liblzma-5.dll to native\liblzma.dll
               (the MinGW DLL also needs its libgcc/libwinpthread DLLs next to it)

.EXAMPLE
    .\tools\build-liblzma.ps1
    .\tools\build-liblzma.ps1 -Tag v5.6.4 -Arch ARM64
#>
param(
    [string]$Tag = "v5.8.1",
    [ValidateSet("x64", "ARM64", "Win32")][string]$Arch = "x64"
)

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$work = Join-Path $root "build\xz"
$src = Join-Path $work "src"
$bld = Join-Path $work "build-$Arch"
$out = Join-Path $root "native"

function Check($what) { if ($LASTEXITCODE -ne 0) { throw "$what failed (exit code $LASTEXITCODE)." } }

if (-not (Get-Command git -ErrorAction SilentlyContinue)) { throw "git is required (winget install Git.Git)." }

$cmake = (Get-Command cmake -ErrorAction SilentlyContinue).Source
if (-not $cmake) {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\Installer\vswhere.exe"
    if (Test-Path $vswhere) {
        $vs = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
        $candidate = Join-Path $vs "Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe"
        if ($vs -and (Test-Path $candidate)) { $cmake = $candidate }
    }
}
if (-not $cmake) {
    throw "CMake was not found. Install the 'C++ CMake tools for Windows' component of Visual Studio, or: winget install Kitware.CMake"
}
Write-Host "CMake: $cmake"

New-Item -ItemType Directory -Force -Path $work, $out | Out-Null
if (-not (Test-Path (Join-Path $src "CMakeLists.txt"))) {
    Write-Host "Cloning xz $Tag ..."
    git clone --depth 1 --branch $Tag https://github.com/tukaani-project/xz.git $src
    if ($LASTEXITCODE -ne 0 -and $Tag -ne "v5.6.4") {
        Write-Warning "Tag $Tag is not available, trying v5.6.4"
        Remove-Item -Recurse -Force $src -ErrorAction SilentlyContinue
        git clone --depth 1 --branch v5.6.4 https://github.com/tukaani-project/xz.git $src
    }
    Check "git clone"
}

& $cmake -S $src -B $bld -A $Arch `
    -DBUILD_SHARED_LIBS=ON `
    "-DCMAKE_MSVC_RUNTIME_LIBRARY=MultiThreaded" `
    -DBUILD_TESTING=OFF
Check "CMake configure"

& $cmake --build $bld --config Release --target liblzma --parallel
Check "CMake build"

$dll = Get-ChildItem -Path $bld -Recurse -Filter "*lzma*.dll" |
    Where-Object { $_.FullName -match "\\Release\\" } |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $dll) { throw "The build finished, but no lzma DLL was found under $bld." }

Copy-Item $dll.FullName (Join-Path $out "liblzma.dll") -Force
Write-Host "OK: $($dll.Name) -> native\liblzma.dll" -ForegroundColor Green
