param(
    [ValidateSet('health','capabilities','rooms','send','requests','result')][string]$Action = 'health',
    [string]$Recipient,
    [string]$Message,
    [string]$MessageFile,
    [string]$RequestId
)
$ErrorActionPreference = 'Stop'
$connectionPath = Join-Path $env:LOCALAPPDATA 'KakaoRelay\connection.json'
if (-not (Test-Path -LiteralPath $connectionPath)) { throw 'KakaoRelay 앱을 먼저 실행하세요.' }
$connection = Get-Content -LiteralPath $connectionPath -Raw -Encoding UTF8 | ConvertFrom-Json
$headers = @{ Authorization = "Bearer $($connection.token)" }
$route = switch ($Action) {
    'result' {
        if ($RequestId -notmatch '\A[a-zA-Z0-9_-]{1,80}\z') { throw '올바른 RequestId가 필요합니다.' }
        "requests/$RequestId"
    }
    default { $Action }
}
$uri = "$($connection.baseUrl)/v1/$route"
if ($Action -eq 'send') {
    if ($MessageFile) { $Message = [IO.File]::ReadAllText((Resolve-Path -LiteralPath $MessageFile).Path) }
    if (-not $Recipient -or [string]::IsNullOrWhiteSpace($Message) -or -not $RequestId) {
        throw 'Recipient, Message 또는 MessageFile, RequestId가 필요합니다. 재시도에는 같은 RequestId를 사용하세요.'
    }
    $body = @{ requestId = $RequestId; recipient = $Recipient; message = $Message } | ConvertTo-Json -Compress
    $result = Invoke-RestMethod -Uri $uri -Method Post -Headers $headers -ContentType 'application/json; charset=utf-8' -Body ([Text.Encoding]::UTF8.GetBytes($body))
} else {
    $result = Invoke-RestMethod -Uri $uri -Method Get -Headers $headers
}
$result | ConvertTo-Json -Depth 10
