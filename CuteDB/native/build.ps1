<#
.SYNOPSIS
    Builds the CuteDB native accelerator and stages it where the .NET build expects it.

.DESCRIPTION
    Compiles native/cutedb-core with cargo and copies the resulting shared library to
    native/artifacts/<rid>/, which is where CuteDB.csproj looks for it — both to copy next to
    local build output and to pack into the NuGet package under runtimes/<rid>/native.

    Nothing here is required to use CuteDB. The managed library implements the same behaviour and
    is used whenever the accelerator is absent, so a machine with no Rust toolchain builds and
    tests the whole solution normally; it just scans large collections more slowly.

.PARAMETER Target
    A Rust target triple to cross-compile for. Defaults to the host.

.PARAMETER Configuration
    'release' (the default) or 'debug'. Only release is worth benchmarking.

.EXAMPLE
    ./build.ps1
    Builds for this machine.

.EXAMPLE
    ./build.ps1 -Target aarch64-apple-darwin
    Cross-compiles for Apple silicon, assuming the target is installed via rustup.
#>
[CmdletBinding()]
param(
    [string]$Target = '',
    [ValidateSet('release', 'debug')]
    [string]$Configuration = 'release'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path

if (-not (Get-Command cargo -ErrorAction SilentlyContinue)) {
    Write-Host 'cargo was not found. Install Rust from https://rustup.rs to build the accelerator.' -ForegroundColor Yellow
    Write-Host 'CuteDB builds and runs without it; scans of large collections will use the managed path.' -ForegroundColor Yellow
    exit 0
}

# Map a Rust target triple onto the .NET runtime identifier that names the same platform, since
# the two ecosystems spell these differently and the package layout uses the .NET spelling.
function Get-RuntimeIdentifier([string]$triple) {
    switch -Regex ($triple) {
        '^x86_64-pc-windows'    { return 'win-x64' }
        '^aarch64-pc-windows'   { return 'win-arm64' }
        '^x86_64-unknown-linux' { return 'linux-x64' }
        '^aarch64-unknown-linux'{ return 'linux-arm64' }
        '^x86_64-apple-darwin'  { return 'osx-x64' }
        '^aarch64-apple-darwin' { return 'osx-arm64' }
        default { throw "No .NET runtime identifier is mapped for the Rust target '$triple'." }
    }
}

function Get-LibraryName([string]$rid) {
    if ($rid.StartsWith('win-')) { return 'cutedb_core.dll' }
    if ($rid.StartsWith('osx-')) { return 'libcutedb_core.dylib' }
    return 'libcutedb_core.so'
}

if (-not $Target) {
    $hostTriple = (rustc -vV | Select-String -Pattern '^host:\s*(.+)$').Matches[0].Groups[1].Value.Trim()
    $Target = $hostTriple
}

$rid = Get-RuntimeIdentifier $Target
$library = Get-LibraryName $rid

Write-Host "Building cutedb_core for $Target -> $rid" -ForegroundColor Cyan

$cargoArgs = @('build', '--manifest-path', (Join-Path $root 'Cargo.toml'), '--target', $Target)
if ($Configuration -eq 'release') { $cargoArgs += '--release' }

& cargo @cargoArgs
if ($LASTEXITCODE -ne 0) { throw "cargo build failed with exit code $LASTEXITCODE." }

$built = Join-Path $root "target/$Target/$Configuration/$library"
if (-not (Test-Path $built)) { throw "cargo reported success but '$built' is not there." }

$destination = Join-Path $root "artifacts/$rid"
New-Item -ItemType Directory -Force -Path $destination | Out-Null
Copy-Item -Path $built -Destination (Join-Path $destination $library) -Force

$size = [math]::Round((Get-Item (Join-Path $destination $library)).Length / 1KB, 1)
Write-Host "Staged artifacts/$rid/$library ($size KB)" -ForegroundColor Green
