param(
    [ValidateSet('health','capabilities','rooms','send','send-image','requests','result')][string]$Action = 'health',
    [string]$Recipient,
    [string]$Message,
    [string]$MessageFile,
    [string]$RequestId,
    [string]$ImagePath,
    [string]$PersonaId,
    [ValidateSet('astra','grok')][string]$ImageProvider,
    [string]$ImageId,
    [ValidateRange(0,30000)][int]$SendDelayMs = 1000
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
if ($Action -eq 'send-image') {
    if (-not $Recipient -or -not $RequestId) { throw 'Recipient와 RequestId가 필요합니다.' }
    $payload = @{ requestId = $RequestId; recipient = $Recipient }
    if ($ImagePath) {
        if ($PersonaId -or $ImageProvider -or $ImageId) { throw 'ImagePath와 페르소나 이미지 식별자는 함께 사용할 수 없습니다.' }
        $payload.imagePath = (Resolve-Path -LiteralPath $ImagePath).Path
    } elseif ($PersonaId -and $ImageProvider -and $ImageId) {
        $payload.attachment = @{ personaId = $PersonaId; provider = $ImageProvider; imageId = $ImageId }
    } else { throw 'ImagePath 또는 PersonaId/ImageProvider/ImageId를 지정하세요.' }
    $body = $payload | ConvertTo-Json -Depth 5 -Compress
    $result = Invoke-RestMethod -Uri $uri -Method Post -Headers $headers -ContentType 'application/json; charset=utf-8' -Body ([Text.Encoding]::UTF8.GetBytes($body))
} elseif ($Action -eq 'send') {
    if ($MessageFile) { $Message = [IO.File]::ReadAllText((Resolve-Path -LiteralPath $MessageFile).Path) }
    if (-not $Recipient -or [string]::IsNullOrWhiteSpace($Message) -or -not $RequestId) {
        throw 'Recipient, Message 또는 MessageFile, RequestId가 필요합니다. 재시도에는 같은 RequestId를 사용하세요.'
    }
    $body = @{ requestId = $RequestId; recipient = $Recipient; message = $Message; sendDelayMs = $SendDelayMs } | ConvertTo-Json -Compress
    $result = Invoke-RestMethod -Uri $uri -Method Post -Headers $headers -ContentType 'application/json; charset=utf-8' -Body ([Text.Encoding]::UTF8.GetBytes($body))
} else {
    $result = Invoke-RestMethod -Uri $uri -Method Get -Headers $headers
}
$result | ConvertTo-Json -Depth 10
