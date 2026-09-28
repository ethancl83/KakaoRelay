$ErrorActionPreference = 'Stop'
$ProjectRoot = Split-Path -Parent $PSScriptRoot
$DotnetPath = Join-Path $ProjectRoot '.tools\dotnet\dotnet.exe'
if (-not (Test-Path -LiteralPath $DotnetPath)) {
    throw 'Local .NET SDK is missing. Run scripts\Setup.ps1 first.'
}
$env:DOTNET_ROOT = Split-Path -Parent $DotnetPath
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE = 'true'
