# KakaoRelay Gateway API · v1 (앱 v0.7.5)

## 이미지 전송 · POST /v1/send-image

PC의 이미지 파일을 카카오톡 대화방에 첨부합니다. Computer Use, 화면 좌표, 클립보드 없이 `WM_DROPFILES`와 전송 창 HWND로 처리합니다.

```json
{
  "requestId": "image-20260929-001",
  "recipient": "대화방 이름",
  "imagePath": "C:\\Images\\happy.png"
}
```

또는 앱에 등록한 페르소나 이미지 식별자를 사용합니다. `imagePath`와 `attachment` 중 하나만 지정하세요.

```json
{
  "requestId": "persona-20260929-001",
  "recipient": "대화방 이름",
  "attachment": {
    "personaId": "default",
    "provider": "astra",
    "imageId": "0123456789abcdef0123456789abcdef"
  }
}
```

이미지 ID 조회: `GET /v1/personas/{personaId}/images`. 응답의 `appliedProvider`, `versions.<provider>.active`와 `referenceId`를 사용합니다. 예제 ID는 실제 ID로 바꾸세요.
생성·재생성·채택은 앱의 **AI 챗봇 → 페르소나 → 이미지**에서만 합니다. Astra와 Grok 이미지는 페르소나별로 분리됩니다.

```powershell
.\scripts\Relay.ps1 -Action send-image -RequestId 'image-20260929-001' -Recipient '대화방 이름' -ImagePath 'C:\Images\happy.png'
.\scripts\Relay.ps1 -Action send-image -RequestId 'persona-20260929-001' -Recipient '대화방 이름' -PersonaId 'default' -ImageProvider 'astra' -ImageId '실제이미지ID'
```

- 기존 API와 같은 Bearer 인증 및 요청 ID 중복 방지 규칙을 사용합니다. 텍스트와 이미지는 서로 다른 요청 ID로 보냅니다.
- PNG/JPEG/GIF/BMP/WebP 로컬 파일의 절대 경로를 받습니다. URL/네트워크 경로는 받지 않습니다. 확장자와 파일 헤더를 확인하며 실제 처리 가능 여부는 카카오톡에 따릅니다. PNG 실제 발송 검증 완료.
- 응답은 기존 발송 기록 형식에 `kind: "image"`, `attachmentQueued`를 추가합니다. `enterPosted`는 전송 요청, `inputCleared`는 첨부 창 닫힘이며 상대방 수신·읽음 확인이 아닙니다.
- 결과가 불명확하면 `needs-review`로 남깁니다. 같은 요청 ID는 다시 첨부하지 않으며, 새 ID로 자동 재전송하지 마세요.
- 전송용 사본은 내 문서의 `KakaoRelay/attachments`에 보관합니다. 전송 중이거나 결과가 불명확할 때 삭제하지 마세요. 생성 원본과 키·발송 기록은 Git에 포함하지 않습니다.
- 하위 호환을 위해 `POST /v1/send`도 빈 `message`와 `imagePath` 또는 `attachment`를 지원합니다.

앱의 ‘연결 · API’ 탭에서 이 사용법과 OpenAPI JSON 명세를 확인하고 각각 전체 복사할 수 있습니다.

앱을 실행하면 같은 프로세스에서 로컬 HTTP API가 시작됩니다. 별도 서버 실행이나 Computer Use가 필요 없습니다.
창의 X를 눌러도 트레이에서 API가 계속 실행됩니다. 트레이의 **완전 종료**로 앱과 API를 종료합니다.
종료 요청 시 새 발송 접수를 막고 진행 중 작업의 기록을 마칩니다. 카카오톡과 보낼 대화방은 열려 있어야 합니다.

## 연결

매번 접속 정보 파일을 읽습니다.

`%LOCALAPPDATA%\KakaoRelay\connection.json`

- `baseUrl`: 이번 실행의 주소. `http://127.0.0.1:<자동 할당 포트>`
- `token`: 이번 실행의 인증 토큰
- `processId`, `version`

모든 요청은 `Authorization: Bearer <token>` 헤더가 필요합니다. 토큰을 로그나 채팅에 출력하지 마세요.
외부 네트워크에는 수신하지 않습니다. 브라우저 Origin/Sec-Fetch-Site 요청과 CORS 요청은 허용하지 않습니다.
앱 재시작 시 주소와 토큰을 다시 읽습니다.

## 가장 짧은 사용법

프로젝트 폴더에서:

```powershell
# 조회만 수행
.\scripts\Relay.ps1 -Action health
.\scripts\Relay.ps1 -Action rooms
.\scripts\Relay.ps1 -Action capabilities

# 실제 발송: 사용자가 요청한 수신방과 본문을 사용
.\scripts\Relay.ps1 -Action send -RequestId 'report-20260928-evening' -Recipient '대화방 이름' -Message '보낼 본문'

# 긴 본문: UTF-8 파일을 그대로 읽음
.\scripts\Relay.ps1 -Action send -RequestId 'report-20260928-evening' -Recipient '대화방 이름' -MessageFile '.\report.txt'

# 응답 유실/대기 후 결과 조회
.\scripts\Relay.ps1 -Action result -RequestId 'report-20260928-evening'
```

동일 업무에는 동일한 요청 ID를 유지합니다. 예제의 send 명령은 실제 메시지를 보냅니다.
발송을 지시받은 AI는 조회 결과의 정확한 대화방 이름과 본문을 보내면 됩니다. HWND, 프로세스 ID, 회색 안내 확인값은 앱이 처리합니다.
화면 클릭, 입력창 타이핑, 보내기 버튼 클릭을 별도로 하지 마세요.

## HTTP 명세

기계 판독용: [OpenAPI 3.1](openapi.json)

| 요청 | 기능 |
| --- | --- |
| GET /v1/health | 앱/API 실행 상태 |
| GET /v1/capabilities | 기능 지원 범위 |
| GET /v1/rooms | 현재 열린 대화방. `selectable: true`인 정확한 `title` 사용 |
| POST /v1/send | 요청 ID, 대화방, 본문으로 한 번 발송 처리 |
| GET /v1/requests | 발송 기록 전체, 최신순. 본문 제외 |
| GET /v1/requests/{requestId} | 특정 요청의 저장된 상태 |

POST /v1/send:

```json
{
  "requestId": "report-20260928-evening",
  "recipient": "대화방 이름",
  "message": "첫 줄\n둘째 줄",
  "sendDelayMs": 1000
}
```

requestId는 영문·숫자·하이픈·밑줄 1~80자입니다. 이 제한은 파일로 보존하는 요청 식별자에만 적용합니다.
본문에는 앱 자체의 글자 수 제한을 두지 않습니다. 공백뿐인 본문과 NUL 문자는 거부합니다.
`sendDelayMs`는 입력 요청이 반환된 뒤 Enter를 보내기 전 대기 시간입니다. 생략하면 1000ms, 허용 범위는 0~30000ms입니다. 본문 입력 후 이 시간만큼 기다리고, 입력 내용을 다시 비교하지 않고 Enter를 한 번 보냅니다. 본문 입력 응답의 2초 시간 초과도 같은 요청 안에서 추가 대기 후 전송으로 이어집니다. 접근 거부, 입력 전 선택 실패, 대상 창 변경은 중단합니다. 입력 전 기존 초안 보호는 유지하며 카카오톡 자체 제한을 우회하지 않습니다.

`inputResponseTimedOut`는 본문 입력 응답이 시간 초과했는지, 응답의 `sendDelayMs`는 사용한 대기 시간을 나타냅니다. `inputCleared`는 Enter 이후 입력창이 비워졌는지 별도로 확인한 결과입니다. 본문 확인을 생략하므로 실제 입력 누락 여부는 판정하지 않습니다. 이미지 첨부에는 이 대기를 적용하지 않습니다.

같은 요청 ID의 대기 시간만 바꿔도 기존 결과를 반환하며 다시 보내지 않습니다. 스크립트에서는 `-SendDelayMs 1500`으로 지정할 수 있습니다.

HTTP 200은 **처리 결과가 반환되었다는 뜻**입니다. 반드시 `status`, `enterPosted`, `inputCleared`를 읽으세요.

| status | 의미 |
| --- | --- |
| blocked-before-input | 대상 또는 초안 상태 때문에 입력 전 중단 |
| inputting / dispatching / prepared | 중단되었거나 아직 완료되지 않은 기록. 자동 재전송 금지 |
| needs-review | 결과 확인 필요. enterPosted=true는 Enter 요청을 큐에 넣었다는 뜻 |
| observed-in-chat | 사람이 실제 새 말풍선을 보고 앱에서 확인한 기록 |
| continued-after-visual-review | 과거 시험에서 별도 후속 요청으로 처리한 기록 |

대표 응답:

```json
{
  "requestId": "report-20260928-evening",
  "recipient": "대화방 이름",
  "status": "needs-review",
  "enterPosted": true,
  "inputCleared": true,
  "detail": "Enter was queued once. Verify the new outgoing bubble; do not resend."
}
```

입력창 비움은 상대방 수신/읽음 확인이 아닙니다. API에 거짓 완료를 만들 수 있는 확인 처리 엔드포인트는 없습니다.

## 중복 및 오류 처리

- 같은 requestId + 같은 대화방/본문: 저장된 결과 반환. 창을 닫고 다시 열어도 다시 보내지 않습니다.
- 같은 requestId + 다른 대화방/본문: 409 request_id_conflict.
- 자동답장·API의 텍스트/이미지 발송이 겹치면 공용 대기열에서 접수 순서대로 처리합니다. 진행 중인 수동 발송이나 앱 작업도 완료될 때까지 기다리며, HTTP 응답은 해당 요청 처리가 끝난 뒤 반환됩니다.
- 클라이언트 연결이 끊겨도 접수된 발송은 계속 처리됩니다. 시간 초과 시 같은 requestId로 조회하거나 동일 요청을 다시 호출하세요. 중복 요청은 한 번만 발송합니다.
- 대기열은 메모리에 있으며 앱 재시작 시 복원되지 않습니다. 종료 중 새 요청과 아직 발송을 시작하지 않은 대기 요청은 503 shutting_down으로 반환됩니다.
- 수신방이 없거나 동명이 모호함: 409 room_unavailable. 대상이 확인되기 전에는 요청을 예약하지 않습니다.
- 요청 예약 후 기록이 불명확하게 중단됨: 409 uncertain. 새 ID로 자동 재전송하지 않습니다.
- 잘못된 JSON/식별자/본문: 400 invalid_request.
- 토큰 없음/불일치 또는 브라우저 요청: 401 unauthorized.
- 결과 없음: 404 receipt_unavailable. 전송되지 않았다는 증거로 해석하지 마세요.
- 예기치 않은 서버 오류: 500 unknown_result. 같은 ID의 결과를 조회하고 새 ID로 자동 재전송하지 마세요.
- 완전 종료 진행 중: 503 shutting_down. 같은 요청 ID를 유지하세요.

연결 실패나 시간 초과가 나도 새 요청 ID를 만들지 마세요. 원래 요청이 처리되었을 수 있습니다.
요청 지문과 결과는 로컬 파일에 보존하지만 본문은 저장하지 않습니다.
기존 초안 보호는 유지합니다. 정확히 `메시지 입력`인 문자열은 사용자의 요청에 따라 빈 안내로 자동 취급합니다.
따라서 동일 문구를 실제 초안으로 작성해 둔 경우도 구분할 수 없습니다.

## 최근 메시지

v0.6 Windows 앱은 `capabilities.recentMessages.supported=true`를 반환합니다. 실행 중인 카카오톡에서 검증된 키로 DB/WAL을 읽으며 키를 확인하지 못한 방은 카카오톡에서 연 뒤 새로고침해야 합니다.

| 요청 | 동작 |
| --- | --- |
| GET /v1/local/rooms | 키를 다시 확인하고 로컬 방 목록 반환. `profile`, `id`, `title`, `readable` 사용 |
| GET /v1/local/messages?profile=…&roomId=…&limit=100 | 최근 문맥 반환. 먼저 방 목록 조회. limit 1~500 |
| GET /v1/ai/settings | 페르소나·프로바이더 설정 |
| PUT /v1/ai/settings | 설정 전체 검증·저장 |
| GET /v1/ai/providers | 설치된 CLI 경로·상태. 로그인 성공을 뜻하지 않음 |
| POST /v1/ai/generate | 분석·답변·질문 응답. 카카오톡에 전송하지 않음 |

웹검색을 사용할 수 있으며 검색한 답변에는 출처 URL을 포함하도록 안내합니다. Windows에서 `reply`/`chat` 요청이 새 이미지 생성을 요구하면 결과에 검증된 `imagePath`가 반환됩니다. 해당 경로를 `POST /v1/send-image`로 보내고 발송 결과를 확인한 다음 `text`를 보내세요. `imageError`가 있으면 생성 실패이며 `text`도 실패 안내로 대체됩니다. 자동답장은 이 순서를 앱이 처리합니다. `targetMessageId`를 지정하면 그 메시지를 현재 답변 대상으로 안내합니다.

```json
{"profile":"방 목록에서 받은 profile","roomId":"방 목록에서 받은 id","mode":"analyze","instruction":"결정된 내용과 할 일을 정리해줘"}
```

`mode`는 `analyze`, `reply`, `chat`입니다. 결과는 `text`, `provider`, `model`, `effort`, `mode`, `room`, `messageCount`, `attempts`를 포함합니다. `auto`는 Codex부터, 직접 선택은 해당 프로바이더부터 시작합니다. 실패하면 Codex → Grok → Claude → Codex 순환으로 최대 2회전 후 503으로 종료합니다. 비활성 프로바이더는 건너뛰며 사용자 취소 시 즉시 중단합니다. 설정은 [AI 사용법](AI.md)을 참고하세요.

키 없음·DB 변경 중·AI 처리 중은 409, 방 미조회는 404, CLI 모두 실패는 503 `ai_unavailable`입니다. 생성 취소는 HTTP 요청을 중단하면 됩니다. 발송과 생성은 별도 요청이며 생성 결과를 `POST /v1/send`에 전달할 때는 기존 발송 규칙을 따르세요. 자동 답장 시작/중지는 Windows UI에서만 제공합니다.

근거: [UI Automation TextPattern](https://learn.microsoft.com/en-us/dotnet/framework/ui-automation/ui-automation-textpattern-overview),
[카카오 메시지 API](https://developers.kakao.com/docs/ko/kakaotalk-message/rest-api).
