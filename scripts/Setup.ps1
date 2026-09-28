$ErrorActionPreference = 'Stop'
$ProjectRoot = Split-Path -Parent $PSScriptRoot
$TaskTools = Join-Path $ProjectRoot '.tools'
New-Item -ItemType Directory -Force -Path $TaskTools | Out-Null
$Installer = Join-Path $TaskTools 'dotnet-install.ps1'
Invoke-WebRequest -Uri 'https://dot.net/v1/dotnet-install.ps1' -OutFile $Installer
& $Installer -Version '10.0.401' -Architecture x64 -InstallDir (Join-Path $TaskTools 'dotnet') -NoPath
if (-not (Test-Path -LiteralPath (Join-Path $TaskTools 'dotnet\sdk\10.0.401'))) { throw 'SDK installation failed.' }
Write-Output 'Project-local SDK is ready. System PATH has not been changed.'
