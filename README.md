<img src="src/KakaoRelay.App/Assets/KakaoRelay.png" width="88" alt="KakaoRelay icon">

# KakaoRelay

Windows 카카오톡의 열린 대화방에 메시지를 보내는 데스크톱 앱과 로컬 API 게이트웨이입니다.
AI가 화면을 클릭하지 않고 대화방·본문·요청 ID를 전달하면 앱이 발송을 처리합니다.

**v0.7**: Windows 핸들 기반 이미지 첨부 API (`POST /v1/send-image`), 방별 페르소나 3개와 Astra/Grok 감정 이미지 관리를 추가했습니다. 앱은 490×760 고정 크기이며, 챗봇은 자동답장 → 페르소나 → 프로바이더 → 대화분석 순서입니다. 연결된 봇 이름을 대화방 목록 칩으로 표시하고, 호출어는 `@봇이름`으로 고정됩니다. [이미지 전송 명세](docs/API.md)

**v0.6 AI 챗봇**: 로컬 대화 분석·답변 생성, 페르소나, Codex → Grok → Claude CLI 자동 선택과 모델/effort 설정을 지원합니다.
[AI 사용법](docs/AI.md) · [macOS/Linux 코드와 빌드](docs/PORTABLE.md)

## 주요 기능

- **AI 챗봇 / 메시지 / API / 지식베이스** 탭을 갖춘 WPF 화면. 메시지 아래에 발송 이력을 함께 표시합니다.
- 옵시디언 보관함 연결, 파일 끌어놓기·일괄 추가, 노트 직접 작성·검색, 대화 요약 저장
- 앱 자체의 본문 글자 수 제한 없이 한글·여러 줄 메시지 작성
- 발송 후 입력창 비움이 확인되면 작성란 자동 초기화
- 같은 요청 ID의 중복 발송 방지, 오류·불확실한 결과 보존
- 창을 닫아도 **트레이에서 API 계속 실행**
- 트레이 왼쪽 클릭으로 복원, 우클릭 메뉴의 **열기 / 완전 종료**로 창 복원·앱 종료
- 이미 실행 중일 때 다시 실행하면 기존 창 복원
- 실행 파일·창·트레이 아이콘

카카오 공식 앱/API가 아닌 독립적인 로컬 도구입니다. 카카오톡 업데이트에 따라 동작이 달라질 수 있습니다.

## 실행

Windows 11 x64 기준입니다. 배포본을 빌드한 뒤 프로젝트의 **실행.cmd** 또는
**artifacts/app/KakaoRelay.exe**를 실행합니다. 배포 시 `artifacts/app/` 폴더 전체가 필요합니다.
DLL은 별도 .NET 설치 없이 실행하기 위한 런타임·UI·API 구성요소입니다. 임의로 삭제하지 마세요. 번역 리소스는 한국어만 포함합니다.

1. 카카오톡에서 보낼 대화방을 열어 둡니다.
2. 앱에서 대화방을 고릅니다. 새 방을 열었다면 **↻**를 누릅니다.
3. 본문을 작성하고 **보내기**를 누릅니다.
4. 실제 새 말풍선을 확인했다면 **대화 확인 완료**로 기록합니다.

창의 **X**는 트레이로 숨깁니다. 작성 중인 본문과 API는 유지됩니다.
완전히 종료하려면 트레이 아이콘을 우클릭하고 **완전 종료**를 선택합니다.
창이 열려 있거나 최소화·숨김 상태여도 같은 우클릭 메뉴를 사용할 수 있습니다.
진행 중인 작업이 있다면 결과 기록을 마친 뒤 종료합니다. Windows 로그인 시 자동 시작은 설정하지 않습니다.

## 옵시디언 지식베이스

**API 옆 지식베이스 탭**에서 자료와 봇의 연결을 관리합니다.

1. **연결 설정**에서 보관함 폴더를 선택하거나 새로 만듭니다. **이 봇의 답변에 지식 노트 사용**을 켜고 **연결 저장**을 누릅니다. **모든 봇에 같은 연결 적용**으로 공유할 수도 있습니다.
2. **자료 → 자료 추가** 또는 파일 끌어놓기로 Markdown, TXT, PDF, Word(`.docx`)를 추가합니다. 원본은 유지하고 읽어 낸 텍스트를 보관함의 `자료/`에 Markdown 노트로 저장합니다. 긴 문서는 자동으로 나눕니다.
3. **직접 쓰기**로 내용을 붙여 넣거나 노트를 작성합니다. 목록에서 미리 보고 **옵시디언에서 편집**으로 수정할 수 있습니다. 검색은 AI 호출 없이 로컬에서 실행됩니다.
4. 대화도 쌓으려면 **AI 챗봇 → 대화분석**에서 분석 결과를 검토한 뒤 **요약을 노트로 저장**을 누릅니다.

한 번에 50개·파일당 20MB까지 추가할 수 있습니다. PDF는 200쪽 이하의 텍스트 PDF를 지원하며 스캔본·이미지는 별도 문자 인식이 필요합니다. 문서의 이미지·첨부 파일·서식은 가져오지 않습니다. TXT는 UTF-8 또는 BOM이 있는 Unicode 형식을 사용하세요.

봇은 옵시디언이 꺼져 있어도 현재 노트를 읽으며, 수정·삭제는 다음 질문부터 반영합니다. 관련 발췌가 선택된 AI 서비스에 전달됩니다. 각 봇에 별도 보관함을 연결할 수 있습니다. [검색 범위와 제한](docs/AI.md#옵시디언-지식베이스)

로컬 연결에는 옵시디언 계정이나 유료 Sync가 필요하지 않습니다. GitHub 백업은 보관함을 **본인 소유 비공개 저장소**에 연결하고 옵시디언의 [Git 커뮤니티 플러그인](https://github.com/Vinzent03/obsidian-git)을 설정해 사용합니다. 주기적 백업은 옵시디언이 실행 중일 때 작동합니다. 개인 노트와 로그인 정보는 이 앱의 소스 저장소에 넣지 않습니다.

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
| POST /v1/send-image | 로컬 파일 또는 페르소나 이미지 첨부 발송 |
| GET /v1/personas/{personaId}/images | 적용된 이미지와 후보 목록 조회 |
| GET /v1/requests | 발송 기록 |
| GET /v1/requests/{requestId} | 개별 결과 |

[API 명세와 예제](docs/API.md) · [OpenAPI 3.1](docs/openapi.json)

앱의 **API** 탭에서도 사용법·예제와 OpenAPI JSON 전체를 읽을 수 있습니다. **복사** 버튼으로 AI 도구에 전달할 내용을 바로 복사하세요.

## 동작 범위

- 현재 사용자 세션에 열려 있고 최소화되지 않은 대화방을 지원합니다.
- 이름이 같은 창이 여러 개면 수신방을 확정하지 않고 중단합니다.
- 기존 초안은 덮어쓰지 않습니다. 정확히 `메시지 입력`인 문자열은 빈 안내로 자동 취급하므로 같은 문구의 실제 초안과는 구분하지 못합니다.
- HTTP 200이나 입력창 비움은 상대방의 수신·읽음 확인이 아닙니다. `status`, `enterPosted`, `inputCleared`를 함께 확인하세요.
- 카카오톡 자체의 길이·입력 제한은 우회하지 않습니다. 입력한 내용이 원문과 다르면 Enter를 보내지 않습니다.
- 오류나 중단 후 새 요청 ID로 자동 재전송하지 않습니다.
- 최근 대화 읽기는 메모리에서 키를 확인한 SQLCipher 4 방에 한해 지원합니다. 닫힌 방 열기, 예약 발송, MCP·외부 업무 시스템 연동은 아직 미지원입니다.

최근 대화는 DB와 WAL의 커밋 기록을 검증해 읽습니다. [구현 범위](docs/AI.md) · [초기 조사 기록](docs/LOCAL-DATA.md)

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
