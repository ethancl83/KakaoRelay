# PC 저장 대화 데이터 조사 · 2026-09-28

최근 메시지는 화면 OCR이 아니라 PC에 저장된 대화 DB에서 읽는 방향이다.

## 이 PC에서 확인한 사실

- 경로: `%LOCALAPPDATA%/Kakao/KakaoTalk/users/<프로필>/chat_data/`
- 방별 `chatLogs_<ID>.edb`, 목록 `chatListInfo.edb`, 일부 `-wal`/ `-shm` 파일이 있다.
- 과거 프로필과 최근 수정되는 프로필이 함께 있다. 수정 시각만으로 현재 로그인 계정을 확정하면 안 된다.
- 최근 수정 프로필의 채팅 DB 2개와 목록 DB 1개를 읽기 전용 공유 모드로 열어 첫 32바이트를 확인했다.
- 세 파일 모두 평문 SQLite의 `SQLite format 3` 헤더가 없다. 암호화 데이터라는 기존 연구와 일치하지만, 헤더만으로 현재 암호화 알고리즘을 확정할 수는 없다.
- 원본 파일을 수정하거나 대화 본문을 추출하지 않았다.
- 현재 API는 최근 메시지 조회를 구현하지 않았으며 지원 여부를 false로 반환한다.

## 구현에 필요한 검증

1. 현재 카카오톡 버전·로그인 프로필과 DB를 정확히 연결한다.
2. 현재 형식에 맞는 암호화 읽기와 키 처리를 실제 표본에서 검증한다.
3. DB와 WAL의 일관된 읽기 스냅샷을 사용한다. DB 파일 수정 시각만으로 새 메시지를 판단하지 않는다.
4. 방 ID/제목, 발신자 ID/이름, 메시지 ID/시각/본문을 연결한다.
5. 연속 조회는 메시지 ID를 기준으로 하며, 편집/삭제/시스템 메시지를 구분한다.
6. 이 검증을 통과한 뒤 최근 메시지 조회 API를 명세에 추가한다. 화면 캡처를 대체 데이터로 섞지 않는다.

## 외부 연구와 불확실성

[2019 DFRWS 연구](https://dfrws.org/sites/default/files/session-files/2019_EU_paper-digital_forensic_analysis_of_encrypted_database_files_in_instant_messaging_applications_on_windows_operating_systems_case_study_with_kakaotalk_nateon_and_qq_messenger.pdf)는
Windows 카카오톡의 암호화 DB 구조를 분석했다. 오래된 결과이므로 현재 버전에 그대로 적용하지 않는다.

[2026 IceKakao 개발자의 실험 기록](https://icenovel.com/projects/icekakao/icekakao-pc-phase-0)은
SQLCipher 4와 실행 프로세스의 키를 사용한 방별 복호화 성공을 보고한다.
이는 해당 개발자의 환경에서의 결과이며, 이 PC에서 같은 방식으로 복호화된다는 검증은 아직 하지 않았다.
따라서 파일이 존재한다는 사실과 최근 메시지를 실제 읽을 수 있다는 주장을 구분한다.
