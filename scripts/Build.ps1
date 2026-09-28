param([switch]$SkipTests)
$OutputPath = 'artifacts\app'
. (Join-Path $PSScriptRoot 'Initialize.ps1')
Push-Location $ProjectRoot
try {
    & $DotnetPath build 'KakaoRelay.slnx' -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    if (-not $SkipTests) {
        & $DotnetPath run --project 'tests\KakaoRelay.Tests' -c Release --no-build
        if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
    }
    & $DotnetPath publish 'src\KakaoRelay.App' -c Release -r win-x64 --self-contained true -o $OutputPath -p:DebugType=None -p:DebugSymbols=false
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
    $PublishedExe = [System.IO.Path]::GetFullPath((Join-Path (Join-Path $ProjectRoot $OutputPath) 'KakaoRelay.exe'))
    $RelativeExe = [System.IO.Path]::GetRelativePath($ProjectRoot, $PublishedExe)
    if ($RelativeExe -match '[%"\r\n]') { throw 'Published path cannot be represented safely in 실행.cmd.' }
    [System.IO.File]::WriteAllText((Join-Path $ProjectRoot '실행.cmd'), "@echo off`r`nstart `"`" `"%~dp0$RelativeExe`"`r`n", [System.Text.UTF8Encoding]::new($false))
    Write-Output (Join-Path (Join-Path $ProjectRoot $OutputPath) 'KakaoRelay.exe')
} finally { Pop-Location }
