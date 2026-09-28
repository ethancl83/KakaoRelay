<img src="src/KakaoRelay.App/Assets/KakaoRelay.png" width="88" alt="KakaoRelay icon">

# KakaoRelay

Windows 카카오톡의 열린 대화방에 메시지를 보내는 데스크톱 앱과 로컬 API 게이트웨이입니다.
AI가 화면을 클릭하지 않고 대화방·본문·요청 ID를 전달하면 앱이 발송을 처리합니다.

## 주요 기능

- **발송 / 이력 / 연결·API** 탭을 갖춘 간결한 WPF 화면
- 앱 자체의 본문 글자 수 제한 없이 한글·여러 줄 메시지 작성
- 발송 후 입력창 비움이 확인되면 작성란 자동 초기화
- 같은 요청 ID의 중복 발송 방지, 오류·불확실한 결과 보존
- 창을 닫아도 **트레이에서 API 계속 실행**
- 트레이 왼쪽 클릭 또는 **KakaoRelay 열기**로 복원, **완전 종료**로 앱과 API 종료
- 이미 실행 중일 때 다시 실행하면 기존 창 복원
- 실행 파일·창·트레이 아이콘

카카오 공식 앱/API가 아닌 독립적인 로컬 도구입니다. 카카오톡 업데이트에 따라 동작이 달라질 수 있습니다.

## 실행

Windows 11 x64 기준입니다. 배포본을 빌드한 뒤 프로젝트의 **실행.cmd** 또는
**artifacts/app/KakaoRelay.exe**를 실행합니다. 배포 시 `artifacts/app/` 폴더 전체가 필요합니다.

1. 카카오톡에서 보낼 대화방을 열어 둡니다.
2. 앱에서 대화방을 고릅니다. 새 방을 열었다면 **↻**를 누릅니다.
3. 본문을 작성하고 **보내기**를 누릅니다.
4. 실제 새 말풍선을 확인했다면 **대화 확인 완료**로 기록합니다.

창의 **X**는 트레이로 숨깁니다. 작성 중인 본문과 API는 유지됩니다.
완전히 종료하려면 트레이 아이콘을 우클릭하고 **완전 종료**를 선택합니다.
진행 중인 작업이 있다면 결과 기록을 마친 뒤 종료합니다. Windows 로그인 시 자동 시작은 설정하지 않습니다.

## AI / API 사용

앱을 실행하면 `127.0.0.1`의 자동 할당 포트에서 API가 함께 시작됩니다.
현재 연결 정보는 `%LOCALAPPDATA%/KakaoRelay/connection.json`에 저장됩니다.
매 실행 바뀌는 Bearer 토큰이 필요하며, 토큰을 로그·채팅·Git에 올리지 마세요.

```powershell
# 연결과 대화방 조회
.\scripts\Relay.ps1 -Action health
.\scripts\Relay.ps1 -Action rooms

# 실제 발송: 대화방 이름과 본문을 바꿔 사용하세요.
.\scripts\Relay.ps1 -Action send -RequestId 'sample-report-001' -Recipient '대화방 이름' -Message '보낼 메시지'

# 결과 조회: 응답이 유실되어도 같은 요청 ID 유지
.\scripts\Relay.ps1 -Action result -RequestId 'sample-report-001'
```

| API | 용도 |
| --- | --- |
| GET /v1/health | 실행 상태 |
| GET /v1/capabilities | 지원 범위 |
| GET /v1/rooms | 열린 대화방 |
| POST /v1/send | 메시지 발송 |
| GET /v1/requests | 발송 기록 |
| GET /v1/requests/{requestId} | 개별 결과 |

[API 명세와 예제](docs/API.md) · [OpenAPI 3.1](docs/openapi.json)

## 동작 범위

- 현재 사용자 세션에 열려 있고 최소화되지 않은 대화방을 지원합니다.
- 이름이 같은 창이 여러 개면 수신방을 확정하지 않고 중단합니다.
- 기존 초안은 덮어쓰지 않습니다. 정확히 `메시지 입력`인 문자열은 빈 안내로 자동 취급하므로 같은 문구의 실제 초안과는 구분하지 못합니다.
- HTTP 200이나 입력창 비움은 상대방의 수신·읽음 확인이 아닙니다. `status`, `enterPosted`, `inputCleared`를 함께 확인하세요.
- 카카오톡 자체의 길이·입력 제한은 우회하지 않습니다. 입력한 내용이 원문과 다르면 Enter를 보내지 않습니다.
- 오류나 중단 후 새 요청 ID로 자동 재전송하지 않습니다.
- 최근 대화 읽기, 닫힌 방 검색/열기, 예약 발송, MCP·외부 업무 시스템 연동은 아직 미지원입니다.

최근 대화 조회는 PC의 로컬 저장 DB를 읽는 방향으로 조사했습니다.
암호화 형식의 읽기 연동은 미구현입니다. [조사와 남은 검증](docs/LOCAL-DATA.md)

## 개인정보와 저장 파일

발송 이력은 `%LOCALAPPDATA%/KakaoRelay/test-sends/`에 보존됩니다.
이력에는 수신방 이름·시각·요청 지문·처리 결과가 들어가며 **본문은 저장하지 않습니다**.
작성란은 메모리에만 유지되며 완전 종료 시 사라집니다.
진단은 창 제목과 컨트롤 정보를 포함하지만 대화 본문은 수집하지 않습니다.

저장소에는 예제·소스·문서·아이콘만 포함합니다. 실행 중 생성되는 토큰, 이력, 요청 파일, 로그,
DB, 내보낸 대화, 화면 캡처, 개발 도구와 빌드 산출물은 `.gitignore`로 제외합니다.
Git 작성자 이메일은 GitHub 비공개 이메일 주소를 사용하는 것을 권장합니다.

## 개발과 빌드

C# / .NET 10 / WPF / ASP.NET Core Kestrel. 트레이는 Windows Forms NotifyIcon을 사용합니다.
SDK는 프로젝트 안에 설치하며 시스템 PATH를 바꾸지 않습니다.

```powershell
# 최초 도구 준비
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Setup.ps1

# 빌드 + 자동 검증 + 런타임 포함 배포본
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Build.ps1

# 읽기 전용 진단
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Diagnose.ps1
```

아이콘 원본은 `src/KakaoRelay.App/Assets/KakaoRelay.svg`이며,
`scripts/Build-Icon.ps1`로 16~256px ICO를 다시 만들 수 있습니다.

```text
src/KakaoRelay.App/   화면, 트레이, 단일 인스턴스, 아이콘
src/KakaoRelay.Core/  전송 엔진, API, 기록, 진단
tests/               대상·초안·중복·중단·HTTP 통합 검증
scripts/             SDK 준비, 빌드, API 호출
docs/                API 명세, 설계, 검증 결과
```

[설계](docs/ARCHITECTURE.md) · [검증](docs/VALIDATION.md) · [아이콘 생성 기록](docs/ICON.md)
