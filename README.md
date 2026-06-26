# Y_Input — 드라이버급 입력 매크로

키보드·마우스·게임패드 입력을 **드라이버 레벨**에서 발생시키는 매크로 프로그램.
일반 `SendInput`과 달리 OS 입력 스택 하위에 주입하므로 `injected` 플래그가 붙지 않아,
합성 입력을 무시하는 일부 응용에서도 동작한다.

- **백엔드**: C# / .NET 10
- **키보드·마우스**: [Interception](https://github.com/oblitum/Interception) 드라이버 (`InputInterceptor` NuGet)
- **게임패드**: [ViGEmBus](https://github.com/nefarius/ViGEmBus) 가상 Xbox360 (`Nefarius.ViGEm.Client` NuGet)
- **UI**: 시스템 트레이 상주 + 로컬 웹서버(ASP.NET Core) → **기본 브라우저(localhost)** 에서 바닐라 HTML/CSS/JS로 제어
- **드라이버**: 앱에 내장 후 최초 실행 시 자동 설치 (커널 드라이버라 백그라운드 서비스로 존재 — 회피 불가)

> ⚠️ Interception/ViGEm은 자동화·테스트·접근성·싱글플레이 매크로용 정당한 도구입니다.
> EAC/Vanguard 등 커널 안티치트가 도는 **온라인 경쟁 게임**에서의 사용은 해당 게임 약관 위반·탐지
> 대상이 될 수 있습니다. 본 프로젝트는 우회/회피 기능을 포함하지 않습니다.

## 구조

```
YInput.slnx
├─ src/YInput.Core      도메인 모델 + JSON 직렬화 (드라이버 의존성 없음)
├─ src/YInput.Input     입력 백엔드: Interception / ViGEm + DriverProvisioner(자동 설치)
├─ src/YInput.Engine    Player(재생) · Recorder(녹화) · HotkeyManager(전역 핫키)
├─ src/YInput.Host      트레이 호스트 + Kestrel 웹서버 + REST/WS, wwwroot 웹 UI
└─ tests/YInput.Core.Tests   단위 테스트(드라이버 불필요)
```

## 요구 사항
- Windows 10/11 (x64)
- .NET 10 SDK (빌드) / 런타임 — 또는 self-contained 게시본
- 관리자 권한 (커널 드라이버 설치·사용에 필요 — 앱 매니페스트가 자동 요청)

## 빌드 & 실행

```powershell
dotnet build YInput.slnx
dotnet test  tests/YInput.Core.Tests   # 드라이버 없이 검증 가능

# 실행: 관리자 권한이 필요하므로 빌드된 EXE를 (관리자) 로 실행
#   src\YInput.Host\bin\Debug\net10.0-windows\YInput.exe
```

> `dotnet run` 은 매니페스트의 requireAdministrator 때문에 권한 오류가 날 수 있습니다.
> 빌드 후 생성된 **YInput.exe** 를 우클릭 → "관리자 권한으로 실행" 하세요.

실행하면:
1. 최초 1회 드라이버 자동 설치 (Interception은 코드 설치 → **재부팅 필요**, ViGEmBus는 `drivers\` 설치 파일).
2. 트레이 아이콘 상주 → 더블클릭 또는 우클릭 **"UI 열기"** → 브라우저에서 제어.

## 배포 (단일 폴더, 런타임 미설치 PC 지원)

```powershell
dotnet publish src/YInput.Host -c Release -r win-x64 --self-contained `
  -p:PublishSingleFile=false -o dist
```

산출된 `dist\` 폴더를 통째로 배포한다. `dist\wwwroot`(웹 UI)와 `dist\drivers`(드라이버) 포함.
게임패드를 미설치 PC에서 쓰려면 [ViGEmBus 설치 파일](https://github.com/nefarius/ViGEmBus/releases)을
`dist\drivers\` 에 `ViGEmBus_*.exe`(또는 `.msi`)로 넣어 두면 자동 설치된다. (`src\YInput.Host\drivers\README.txt` 참고)

## 사용법
- **녹화**: UI에서 `● 녹화` → 입력 → `■ 정지` → 이름 저장. (실시간 로그로 캡처 확인)
- **재생**: 매크로의 `▶ 재생` (다시 누르면 정지). 반복·속도 배율은 편집에서 조정.
- **전역 핫키**: 편집에서 트리거 핫키 지정 → 다른 창에서도 해당 키로 시작/정지 토글.
- **게임패드**: `게임패드 연결` → `joy.cpl`(게임 컨트롤러 속성)에서 가상 패드 확인 → `패드 A 테스트`.
- **텍스트 매크로**: 편집 → `+ 텍스트 입력` 으로 문자열 타이핑 스텝 추가.

### Interception 디바이스 인식
Interception은 실제 입력이 한 번 들어와야 device id를 알 수 있어, 송출 전
**키를 한 번 누르거나 마우스를 한 번 움직여** 디바이스를 활성화해야 한다(UI 안내 표시).

## 검증
- `dotnet test` — 모델 직렬화 라운드트립, 재생 타이밍/순서, 녹화 로직 (드라이버 불필요)
- 게임패드: 연결 후 `joy.cpl` 에서 버튼/스틱 반응 확인
- 키보드·마우스: 드라이버 설치+재부팅 후, 메모장 타이핑 매크로를 녹화→재생

## 데이터 위치
매크로는 `%APPDATA%\YInput\macros\{id}.json` 에 저장된다. 삭제는 휴지통으로 이동(영구 삭제 안 함).
