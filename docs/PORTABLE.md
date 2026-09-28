# macOS / Linux · KakaoRelay Portable

OS와 무관한 .NET 10 AI 엔진과 로컬 브라우저 UI입니다. Windows WPF 프로젝트를 빌드하지 않는 별도 솔루션을 제공합니다. Node/Python은 런타임·빌드에 필요하지 않습니다.

## 각 OS에서 빌드

.NET 10 SDK와 사용할 AI CLI를 설치한 뒤 저장소 루트에서 실행합니다.

```bash
bash scripts/build-portable.sh
```

스크립트가 macOS/Linux와 x64/arm64를 감지하고 공통 테스트를 실행한 뒤 런타임 포함 실행 파일을 생성합니다.

```bash
# Apple silicon
./artifacts/portable/osx-arm64/KakaoRelay.Portable
# Intel Mac
./artifacts/portable/osx-x64/KakaoRelay.Portable
# Linux x64
./artifacts/portable/linux-x64/KakaoRelay.Portable
# Linux arm64
./artifacts/portable/linux-arm64/KakaoRelay.Portable
```

개발 실행 및 수동 빌드:

```bash
dotnet run --project src/KakaoRelay.Portable
dotnet build KakaoRelay.Portable.slnx -c Release
dotnet run --project tests/KakaoRelay.Portable.Tests -c Release
dotnet publish src/KakaoRelay.Portable -c Release -r osx-arm64 --self-contained true -o artifacts/portable/osx-arm64
```

실행하면 `127.0.0.1`의 임의 포트에만 바인딩하고 기본 브라우저를 엽니다. SSH 등에서는 `--no-browser`로 실행하고 터미널에 표시된 주소를 같은 호스트의 브라우저에서 사용하세요. 주소의 `#` 뒤 값은 해당 실행 인스턴스의 접근 토큰이므로 공유하지 마세요. 종료는 Ctrl+C입니다.

## 기능과 데이터

- 카카오톡 한국어 TXT 내보내기와 KakaoRelay JSON 파일 가져오기
- 분석·질문·답변 초안과 복사
- 페르소나 이름·역할·말투·대화 성향·추가 지침
- Codex → Grok → Claude 자동 전환 또는 프로바이더 직접 선택
- 프로바이더별 실행 경로·모델·effort, 문맥 수·제한 시간 설정
- 취소, CLI 설치 상태 표시

한국어 TXT 형식은 날짜 구분선 아래 `[이름] [오후 1:23] 내용` 또는 `2026년 9월 28일 오후 1:23, 이름 : 내용`을 지원합니다. 시각은 한국 표준시로 해석합니다. 다른 언어·형식은 KakaoRelay JSON으로 변환해서 가져오세요. JSON은 Windows의 **AI 챗봇 → JSON 저장** 결과 또는 `messages` 배열, `messageId/author/timeKST/message` 형식 배열을 지원합니다. `roomId`가 다른 메시지는 다른 방으로 가져옵니다.

가져온 대화와 응답은 프로세스 메모리에만 유지되므로 종료 후에는 다시 가져와야 합니다. 설정은 사용자 LocalApplicationData의 `KakaoRelay/ai-settings.json`에 보존합니다. 대화·모델 출력은 HTML로 실행하지 않고 텍스트로 표시합니다.

## 플랫폼 범위

| 기능 | Windows WPF | macOS/Linux Portable |
| --- | --- | --- |
| AI 분석·질문·페르소나 | 지원 | 지원 |
| CLI 모델·effort·우선순위 | 지원 | 지원 |
| 카카오톡 로컬 DB + WAL 직접 읽기 | 지원되는 SQLCipher 4 방 | 미구현 |
| 가져온 TXT/JSON 분석 | JSON 내보내기 제공 | 지원 |
| 카카오톡 직접 전송·자동 답장 | 지원 | 미구현 |

macOS 카카오톡의 DB 구조·키 추출과 전송 자동화는 별도 어댑터가 필요합니다. Linux에서는 대화 파일 기반으로 동작합니다. Windows 메모리·Win32 코드는 공통 프로젝트에 포함되지 않습니다.

공통 코드는 Windows에서 빌드·자동 검증했습니다. macOS/Linux 실제 실행 검증과 macOS 서명·공증, 배포 패키징은 해당 OS에서 진행해야 합니다.
