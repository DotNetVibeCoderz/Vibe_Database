<#
.SYNOPSIS
    Builds and installs CuteDB Browser on Windows.

.DESCRIPTION
    Publishes a self-contained-except-for-the-runtime build into a directory of your choosing and
    puts a shortcut on the Start menu. Requires the .NET 10 SDK; nothing else.

    Dibuat oleh Gravicode Studios, dipimpin oleh Kang Fadhil.

.PARAMETER InstallPath
    Where to install. Defaults to %LOCALAPPDATA%\CuteBrowser.

.PARAMETER SelfContained
    Bundles the .NET runtime, so the machine running it needs no .NET installed. Roughly 80 MB
    larger.

.PARAMETER NoShortcut
    Skips the Start-menu shortcut.

.EXAMPLE
    ./install.ps1

.EXAMPLE
    ./install.ps1 -InstallPath 'D:\Tools\CuteBrowser' -SelfContained
#>
[CmdletBinding()]
param(
    [string] $InstallPath = (Join-Path $env:LOCALAPPDATA 'CuteBrowser'),
    [switch] $SelfContained,
    [switch] $NoShortcut
)

$ErrorActionPreference = 'Stop'

function Write-Step([string] $Message) {
    Write-Host "==> $Message" -ForegroundColor Yellow
}

# The script lives in tools/CuteBrowser/scripts, so the project is two directories up.
$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$project = Join-Path (Split-Path -Parent $scriptRoot) 'CuteBrowser.csproj'

if (-not (Test-Path $project)) {
    throw "Cannot find CuteBrowser.csproj next to this script. Run it from the repository."
}

Write-Step 'Checking for the .NET SDK'
$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if (-not $dotnet) {
    throw 'The .NET 10 SDK is not on PATH. Install it from https://dotnet.microsoft.com/download and try again.'
}

$sdks = & dotnet --list-sdks
if (-not ($sdks | Where-Object { $_ -match '^10\.' })) {
    Write-Warning "No .NET 10 SDK found. Installed SDKs:`n$($sdks -join "`n")"
    throw 'CuteDB Browser targets net10.0. Install the .NET 10 SDK and try again.'
}

Write-Step "Publishing to $InstallPath"
$arguments = @(
    'publish', $project,
    '-c', 'Release',
    '-r', 'win-x64',
    '-o', $InstallPath,
    '--self-contained', $(if ($SelfContained) { 'true' } else { 'false' })
)

& dotnet @arguments
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE."
}

$exe = Join-Path $InstallPath 'CuteBrowser.exe'
if (-not (Test-Path $exe)) {
    throw "Publish finished but $exe is not there."
}

# The published App.config carries the API keys, so a reinstall must not overwrite one that has
# already been filled in.
$config = Join-Path $InstallPath 'CuteBrowser.dll.config'
if (Test-Path $config) {
    Write-Step 'Settings preserved (CuteBrowser.dll.config was not replaced)'
}

if (-not $NoShortcut) {
    Write-Step 'Creating the Start-menu shortcut'
    $programs = [Environment]::GetFolderPath('Programs')
    $link = Join-Path $programs 'CuteDB Browser.lnk'

    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($link)
    $shortcut.TargetPath = $exe
    $shortcut.WorkingDirectory = $InstallPath
    $shortcut.Description = 'CuteDB Browser - browse, query and explain a CuteDB database'
    $shortcut.Save()

    Write-Host "    $link"
}

Write-Host ''
Write-Host 'CuteDB Browser installed.' -ForegroundColor Green
Write-Host "  Run:      $exe"
Write-Host "  Settings: $config"
Write-Host ''
Write-Host 'Jack, the assistant, needs an API key before he will answer. Set one in'
Write-Host 'Tools > Settings, or set an environment variable:'
Write-Host '  OPENAI_API_KEY, AZURE_OPENAI_API_KEY, ANTHROPIC_API_KEY, GEMINI_API_KEY,'
Write-Host '  OPENAI_COMPATIBLE_API_KEY, TAVILY_API_KEY'
Write-Host 'Ollama needs no key at all and runs on your own machine.'
