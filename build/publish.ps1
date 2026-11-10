<#
.SYNOPSIS
    Publishes Phantom and builds its Windows installer.

.DESCRIPTION
    1. Publishes a self-contained win-x64 build to artifacts\publish\win-x64.
    2. Compiles installer\Phantom.iss with Inno Setup into artifacts\installer.

    Requires the .NET 10 SDK. The installer step requires Inno Setup 6 (https://jrsoftware.org/isinfo.php);
    pass -SkipInstaller to publish only.

.EXAMPLE
    pwsh build\publish.ps1
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$Runtime = 'win-x64',
    [switch]$SkipInstaller
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$publishDir = Join-Path $root "artifacts\publish\$Runtime"

[xml]$props = Get-Content (Join-Path $root 'Directory.Build.props')
$version = ($props.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1)
Write-Host "Publishing Phantom $version ($Runtime, $Configuration)..."

if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
dotnet publish (Join-Path $root 'src\Phantom.App\Phantom.App.csproj') `
    -c $Configuration -r $Runtime --self-contained true `
    -p:PublishReadyToRun=true -p:DebugType=none `
    -o $publishDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE." }

if ($SkipInstaller) {
    Write-Host "Published to $publishDir (installer skipped)."
    return
}

$iscc = (Get-Command iscc.exe -ErrorAction SilentlyContinue).Source
if (-not $iscc) {
    $candidates = @(
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
        "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
    )
    $iscc = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
}
if (-not $iscc) {
    throw 'Inno Setup 6 (ISCC.exe) was not found. Install it from https://jrsoftware.org/isinfo.php or run with -SkipInstaller.'
}

& $iscc "/DAppVersion=$version" "/DSourceDir=$publishDir" (Join-Path $root 'installer\Phantom.iss')
if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed with exit code $LASTEXITCODE." }

Get-ChildItem (Join-Path $root 'artifacts\installer') -Filter '*.exe' | ForEach-Object {
    Write-Host ("Installer: {0} ({1:N1} MB)" -f $_.FullName, ($_.Length / 1MB))
}
