param([string]$OutputPath)
$ErrorActionPreference = 'Stop'
$ProjectRoot = Split-Path -Parent $PSScriptRoot
$AppPath = Join-Path $ProjectRoot 'artifacts\app\KakaoRelay.exe'
if (-not (Test-Path -LiteralPath $AppPath)) { throw 'Run scripts\Build.ps1 first.' }
if (-not $OutputPath) { $OutputPath = Join-Path $ProjectRoot ('artifacts\diagnostics\diagnostic-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [guid]::NewGuid().ToString('N') + '.json') }
$OutputPath = [System.IO.Path]::GetFullPath($OutputPath)
$TaskStart = [System.Diagnostics.ProcessStartInfo]::new($AppPath)
$TaskStart.UseShellExecute = $false
$TaskStart.CreateNoWindow = $true
# ArgumentList is available in PowerShell 7. Windows PowerShell uses a quoted argument string.
$TaskStart.Arguments = '--diagnose "' + $OutputPath + '"'
$TaskProcess = [System.Diagnostics.Process]::Start($TaskStart)
if (-not $TaskProcess.WaitForExit(30000)) { $TaskProcess.Kill(); throw 'Diagnostic coordinator exceeded its deadline.' }
if (-not (Test-Path -LiteralPath $OutputPath)) { throw 'Diagnostic report was not created.' }
$TaskReport = Get-Content -LiteralPath $OutputPath -Raw | ConvertFrom-Json
[pscustomobject]@{ Status=$TaskReport.status; Processes=@($TaskReport.processes).Count; Windows=@($TaskReport.windows).Count; ReadOnly=$TaskReport.readOnly; Report=$OutputPath } | Format-List
if ($TaskProcess.ExitCode -ne 0) { Write-Warning 'Diagnosis ended with partial data or an error. Review the report.' }
