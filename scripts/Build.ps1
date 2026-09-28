param([switch]$SkipTests)
. (Join-Path $PSScriptRoot 'Initialize.ps1')
Push-Location $ProjectRoot
try {
    & $DotnetPath build 'KakaoRelay.slnx' -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    if (-not $SkipTests) {
        & $DotnetPath run --project 'tests\KakaoRelay.Tests' -c Release --no-build
        if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
    }
    & $DotnetPath publish 'src\KakaoRelay.App' -c Release -r win-x64 --self-contained true -o 'artifacts\app' -p:DebugType=None -p:DebugSymbols=false
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
    Write-Output (Join-Path $ProjectRoot 'artifacts\app\KakaoRelay.exe')
} finally { Pop-Location }
