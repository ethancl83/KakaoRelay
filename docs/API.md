# KakaoRelay Gateway API · v1 (앱 v0.5)

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
  "message": "첫 줄\n둘째 줄"
}
```

requestId는 영문·숫자·하이픈·밑줄 1~80자입니다. 이 제한은 파일로 보존하는 요청 식별자에만 적용합니다.
본문에는 앱 자체의 글자 수 제한을 두지 않습니다. 공백뿐인 본문과 NUL 문자는 거부합니다.
카카오톡 자체의 입력·전송 제한은 우회하지 않으며, 입력 결과가 원문과 다르면 Enter를 보내지 않습니다.

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
- 앱에서 다른 작업 처리 중: 409 busy. 잠시 후 같은 ID로 조회하거나 동일 요청을 다시 호출합니다.
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

현재 API는 최근 대화 조회를 제공하지 않습니다. `capabilities.recentMessages.supported=false`로 명확히 반환합니다.
2026-09-28 PC의 `chat_data/chatLogs_*.edb`, `chatListInfo.edb` 및 WAL 파일을 확인했습니다.
표본 파일은 평문 SQLite 헤더가 없었습니다. 암호화 형식과 키 처리, DB/WAL 일관성을 검증한 로컬 읽기 어댑터가 필요합니다.
최근 대화 수집은 이 로컬 데이터 경로를 기준으로 설계합니다. 현재 자동 수집·주기 감시는 구현하지 않았습니다.
입력창 내용을 수신 메시지로 취급하지 않습니다. 상세: [로컬 데이터 조사](LOCAL-DATA.md).

근거: [UI Automation TextPattern](https://learn.microsoft.com/en-us/dotnet/framework/ui-automation/ui-automation-textpattern-overview),
[카카오 메시지 API](https://developers.kakao.com/docs/ko/kakaotalk-message/rest-api).
