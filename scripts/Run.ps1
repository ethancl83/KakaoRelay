param([string]$ReportPath)
$ErrorActionPreference = 'Stop'
$ProjectRoot = Split-Path -Parent $PSScriptRoot
$AppPath = Join-Path $ProjectRoot 'artifacts\app\KakaoRelay.exe'
if (-not (Test-Path -LiteralPath $AppPath)) { throw 'Run scripts\Build.ps1 first.' }
if ($ReportPath) {
    Start-Process -FilePath $AppPath -ArgumentList @('--report', ('"' + [System.IO.Path]::GetFullPath($ReportPath) + '"'))
} else { Start-Process -FilePath $AppPath }
