<#
.SYNOPSIS
    Builds Mosaik Capture: the Rust tray app and the C# capture engine, side by side in bin\.

.DESCRIPTION
    Requires the Rust toolchain (cargo) and the .NET SDK 10 (dotnet). Rust and .NET are
    build-time only; the finished app needs nothing beyond Windows 10/11 x64 with
    .NET Framework 4.8 (built in) and, for screen recording, ffmpeg.

.PARAMETER Test
    Also run the Rust and C# test suites (pixel tests included, they use this machine's fonts).

.PARAMETER Package
    Also zip bin\ into dist\mosaik-capture-<version>-win-x64.zip.

.PARAMETER RustToolchain
    Optional rustup toolchain, e.g. stable-x86_64-pc-windows-gnu on machines without the
    MSVC linker. By default the GNU toolchain is chosen automatically when link.exe is missing.

.EXAMPLE
    ./build.ps1 -Test
#>
[CmdletBinding()]
param(
    [switch]$Test,
    [switch]$Package,
    [string]$RustToolchain
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = $PSScriptRoot
$bin = Join-Path $root 'bin'
$engineProject = Join-Path $root 'engine/MosaikCapture.Engine.csproj'
$solution = Join-Path $root 'MosaikCapture.sln'

function Invoke-Step([string]$Name, [scriptblock]$Command) {
    Write-Host "==> $Name" -ForegroundColor Cyan
    & $Command
    if ($LASTEXITCODE -ne 0) { throw "$Name failed with exit code $LASTEXITCODE" }
}

# Without Visual Studio there is no link.exe; the GNU toolchain links on its own.
if (-not $RustToolchain -and -not (Get-Command link.exe -ErrorAction SilentlyContinue)) {
    $RustToolchain = 'stable-x86_64-pc-windows-gnu'
}
$cargo = @()
if ($RustToolchain) { $cargo += "+$RustToolchain" }

Invoke-Step 'Rust tray app' { cargo @cargo build --release --locked }
Invoke-Step 'C# capture engine' { dotnet build $engineProject -c Release -o $bin }

New-Item -ItemType Directory -Force $bin | Out-Null
$targetDir = if ($env:CARGO_TARGET_DIR) { $env:CARGO_TARGET_DIR } else { Join-Path $root 'target' }
$rustExe = Get-ChildItem $targetDir -Recurse -Filter 'MosaikCapture.exe' |
    Where-Object { $_.Directory.Name -eq 'release' } |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $rustExe) { throw "MosaikCapture.exe not found under $targetDir" }
Copy-Item $rustExe.FullName $bin -Force

if ($Test) {
    Invoke-Step 'Rust tests' { cargo @cargo test --locked }
    Invoke-Step 'C# tests' { dotnet test $solution -c Release }
    # GUI-subsystem executables: wait on the process object to get their exit codes
    foreach ($exe in 'MosaikCapture.exe', 'MosaikCapture.Engine.exe') {
        Write-Host "==> Self-test $exe" -ForegroundColor Cyan
        $report = Join-Path ([IO.Path]::GetTempPath()) 'mosaik-capture-self-test.json'
        $process = [Diagnostics.Process]::Start((Join-Path $bin $exe), "--self-test `"$report`"")
        $process.WaitForExit()
        if ($process.ExitCode -ne 0) { throw "Self-test of $exe failed with exit code $($process.ExitCode)" }
    }
}

if ($Package) {
    $version = (Select-String -Path (Join-Path $root 'Cargo.toml') -Pattern '^version = "(.+)"').Matches[0].Groups[1].Value
    $dist = Join-Path $root 'dist'
    New-Item -ItemType Directory -Force $dist | Out-Null
    $zip = Join-Path $dist "mosaik-capture-$version-win-x64.zip"
    Compress-Archive -Path (Join-Path $bin '*.exe') -DestinationPath $zip -Force
    Write-Host "Package: $zip"
}

Write-Host "Built: $bin" -ForegroundColor Green
