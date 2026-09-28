# 아이콘

단색 청록 타일, 흰 말풍선, 오른쪽 화살표로 구성한 벡터 아이콘입니다.
질감·그라데이션·그림자를 사용하지 않습니다.

- 편집 원본: `src/KakaoRelay.App/Assets/KakaoRelay.svg`
- 미리보기: `src/KakaoRelay.App/Assets/KakaoRelay.png`
- Windows 아이콘: `src/KakaoRelay.App/Assets/KakaoRelay.ico`

`scripts/Build-Icon.ps1`은 SVG 경로를 WPF로 렌더링하여 PNG와 ICO를 만듭니다.
ICO의 16, 20, 24, 32, 40, 48, 64, 128, 256px 프레임은 원본 벡터에서 각각 렌더링합니다.
실행 파일·창·트레이가 같은 아이콘을 사용합니다.
