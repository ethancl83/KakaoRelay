# 구조

## 앱 수명과 트레이

WPF의 OnExplicitShutdown으로 앱 수명을 관리한다. 닫기 요청은 창을 숨기고,
Windows Forms NotifyIcon의 메뉴에서 명시적으로 완전 종료한다.
종료 요청 시 새 작업을 차단하고 진행 중 작업의 영속 기록을 마친 뒤 API와 트레이 리소스를 정리한다.
Windows 로그아웃/종료는 가로막지 않는다.

세션 범위 named mutex로 단일 인스턴스를 유지한다. 두 번째 실행은 named event를 보내
기존 창을 복원한다. 숨겨진 창은 Process.MainWindowHandle로 찾을 수 없으므로 그 값에 의존하지 않는다.
진단 worker는 트레이·API·주 앱 mutex를 만들지 않는다.

## 발송과 API

WPF 화면과 Kestrel은 같은 프로세스에서 실행된다. API는 127.0.0.1의 동적 포트를 사용하며
매 실행 토큰을 connection.json에 기록한다. 외부 인터페이스와 브라우저 Origin 요청은 허용하지 않는다.

UI dispatcher가 UI/API의 작업 시작을 조율한다. ApiSendService는 요청 지문을 먼저 예약하고
TestSender는 영속 기록과 mutex로 요청의 재실행을 방지한다. 예약과 이력에 본문은 저장하지 않는다.
같은 API 요청 ID는 방이 닫힌 뒤에도 기존 결과를 반환한다.

수신방 이름, 현재 프로세스/창 핸들, 입력 컨트롤 클래스와 ID를 확인한다.
EM_REPLACESEL로 본문을 반영한 후 읽어 비교하고 Enter 요청을 게시한다.
포커스 활성화, 클립보드, 물리 키보드 입력을 발송 경로에 사용하지 않는다.
입력창 비움, 대화의 새 말풍선, 상대방 수신/읽음은 서로 다른 상태다.

발송 후 Enter 요청과 입력창 비움이 확인되면 앱 작성란을 자동 초기화한다.
불확실한 결과에서는 본문과 요청 ID를 유지한다. 내용이나 수신방을 바꾸면 새 작성 세션이다.

## 진단과 최근 대화

Win32/UIA 진단은 별도 worker에서 실행한다. 제공자 호출이 멈추면 부모가 제한 시간 후 worker만 종료한다.
UIA는 창 구조와 패턴 지원 여부만 기록한다.

v0.6의 LocalChatReader는 실행 중인 카카오톡에서 DB별 키 후보를 HMAC 검증하고, 안정된 DB/WAL 스냅샷의 마지막 커밋까지 적용한다. Windows SQLite 엔진에서 메모리 DB를 읽으며 원본과 프로세스 메모리를 수정하지 않는다.

KakaoRelay.AI는 net10.0 공통 라이브러리다. 페르소나·CLI 설정 저장, 프롬프트 구성, Codex → Grok → Claude fallback, 취소/시간 제한을 담당한다. WPF 앱과 macOS/Linux용 KakaoRelay.Portable이 공유한다. Portable은 ImportedChatReader와 loopback 브라우저 UI를 사용하고 Win32 전송 코드에 의존하지 않는다. 상세는 [AI 문서](AI.md)와 [Portable 빌드](PORTABLE.md)에 있다.
