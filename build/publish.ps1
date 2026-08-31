<#
.SYNOPSIS
    Publishes Purplemail and, if Inno Setup is present, builds the installer.

.DESCRIPTION
    Produces dist\publish (the app payload) and dist\Purplemail-<version>-Setup.exe.

    Self-contained by default: the .NET runtime ships inside the install, so a recipient needs
    nothing but Windows and the WebView2 runtime (present on all Windows 11 and most Windows 10).
    It costs ~100 MB of payload, which is the right trade for an installer whose entire purpose is
    handing the app to someone else - a framework-dependent build fails at launch on any machine
    without the exact .NET Desktop Runtime, with an error the recipient can do nothing about.

.PARAMETER FrameworkDependent
    Build against an installed .NET runtime instead of bundling it. Much smaller, but the target
    machine must already have the .NET 9 Desktop Runtime.

.PARAMETER SkipInstaller
    Publish only; don't try to run Inno Setup.
#>
[CmdletBinding()]
param(
    [switch]$FrameworkDependent,
    [switch]$SkipInstaller
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'src\EmailClient\EmailClient.csproj'
$outDir = Join-Path $root 'dist\publish'

Write-Host "Publishing $project" -ForegroundColor Cyan
if (Test-Path $outDir) { Remove-Item $outDir -Recurse -Force }

$selfContained = (-not $FrameworkDependent).ToString().ToLower()
Write-Host "  self-contained: $selfContained" -ForegroundColor DarkGray

dotnet publish $project `
    --configuration Release `
    --runtime win-x64 `
    --self-contained $selfContained `
    --output $outDir `
    -p:PublishSingleFile=false `
    -p:DebugType=none
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }

Write-Host "Published to $outDir" -ForegroundColor Green

if ($SkipInstaller) { return }

# winget installs Inno per-user by default, which is neither of the Program Files locations.
$iscc = @(
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe"
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1

if (-not $iscc) {
    Write-Warning "Inno Setup 6 not found - skipping the installer."
    Write-Host  "Install it with:  winget install --id JRSoftware.InnoSetup -e" -ForegroundColor Yellow
    Write-Host  "Then re-run this script." -ForegroundColor Yellow
    return
}

Write-Host "Building the installer with $iscc" -ForegroundColor Cyan
& $iscc (Join-Path $PSScriptRoot 'Purplemail.iss')
if ($LASTEXITCODE -ne 0) { throw "ISCC failed with exit code $LASTEXITCODE" }

Get-ChildItem (Join-Path $root 'dist') -Filter '*Setup.exe' |
    ForEach-Object { Write-Host "Installer: $($_.FullName)" -ForegroundColor Green }
