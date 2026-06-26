이 폴더는 동봉 드라이버 설치 파일을 두는 곳입니다.
(DriverProvisioner가 실행 시 여기서 설치 파일을 찾아 자동 설치합니다.)

[Interception — 키보드·마우스]
  별도 파일이 필요 없습니다. InputInterceptor NuGet에 드라이버가 내장되어
  코드(InputInterceptor.InstallDriver())로 자동 설치됩니다.

[ViGEmBus — 가상 게임패드]
  배포 시 ViGEmBus 설치 파일을 이 폴더에 넣어 주세요. 파일명이 "ViGEmBus"로
  시작하면 자동 인식됩니다(예: ViGEmBus_1.22.0_x64_x86_arm64.exe 또는 .msi).
  미설치 PC에서 게임패드 기능을 쓰려면 필요합니다.
  (이미 ViGEmBus가 설치된 PC에서는 자동 건너뜁니다.)

  공식 배포처: https://github.com/nefarius/ViGEmBus/releases

※ 이 폴더의 파일은 빌드/게시 시 실행 폴더의 drivers\ 로 복사됩니다.
