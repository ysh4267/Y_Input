# Y_Input 작업 방법 (자주 쓰는 명령 모음)

드라이버 레벨 메이플 매크로. .NET 10, 웹 UI(127.0.0.1:48710) + 트레이 앱.
모든 명령은 PowerShell 기준. SDK는 전역 설치돼 `dotnet` 바로 사용
(구버전 문서의 사용자 폴더 경로는 폐기 — 2026-08-05 현행화).

## 빌드·테스트

```powershell
dotnet build "YInput.slnx"
dotnet test  "YInput.slnx"    # 36개 전부 통과해야 함
```

## 게시 → 자동 설치 (한 사이클)

버전은 `0.3.NNN-local` 순번 증가(마지막 설치 버전 +1). 설치 경로: `%LOCALAPPDATA%\Programs\YInput\YInput.exe`.

```powershell
# 1) 게시 (publish/ 는 gitignore됨)
dotnet publish "src\YInput.Host\YInput.Host.csproj" `
  -c Release -r win-x64 --self-contained -p:PublishSingleFile=true `
  -p:Version=0.3.NNN-local -o "publish"

# 2) 종료 요청 → 프로세스 종료 폴링 → 교체 → 재시작 (고정 대기 금지, 폴링)
Invoke-RestMethod -Method Post -Uri "http://127.0.0.1:48710/api/app/quit" -TimeoutSec 3
while (Get-Process -Name YInput -ErrorAction SilentlyContinue) { Start-Sleep -Milliseconds 300 }
Copy-Item "publish\YInput.exe" "$env:LOCALAPPDATA\Programs\YInput\YInput.exe" -Force
Start-Process "$env:LOCALAPPDATA\Programs\YInput\YInput.exe" -ArgumentList "--updated"

# 3) 새 버전 확인 (뜰 때까지 폴링)
Invoke-RestMethod -Uri "http://127.0.0.1:48710/api/app/version"   # current 확인
```

## 룬 모듈 지도 (증상 → 열 파일, 2026-08-04 모듈화)

| 증상/수정 대상 | 파일 |
|---|---|
| 줄 채택이 잘못됨(잡줄·밀린 줄·중심/y-밴드 게이트·에지 보정·부분/웜톤 줄) | `src\YInput.Host\Vision\Rune\RuneArrowDetector.Row.cs` |
| 화살표 추출·방향 분류·시그니처(채도·차분 마스크 체인 포함) | `Vision\Rune\RuneArrowDetector.cs` (+프리미티브는 `.Mask.cs`) |
| 각도 시계열·머리 플립·회전 판정·반동 투표 | `Vision\Rune\RuneAngleTracker.cs` |
| 잠금·재선출·재배치·확정(중복 관측·X순 입력) — 퍼즐 판정 전부 | `Vision\Rune\RunePuzzleSolver.cs` |
| 소스 간 후보 융합(④·⑦·⑦w 실패 시 최후 위치 폴백 — 군집·교차확인 게이트) | `Vision\Rune\RuneArrowDetector.Fusion.cs` |
| 룬 감지→이동→발동→입력→검증 흐름·증거 저장·캡처 스케줄 | `src\YInput.Host\PositionWatcher.Rune.cs` |
| 오프라인 재현 CLI(--rune-analyze) | `Vision\Rune\RuneArrowDetector.Offline.cs` |
| 미니맵 룬 아이콘 | `Vision\MinimapDetector.cs` |

스트립 리플레이는 실전과 **같은 RunePuzzleSolver를 구동**한다(미러 없음) — 실전 판정 수정은
오프라인 재현에 자동 반영된다. 솔버는 벽시계를 읽지 않으므로(시각 주입) 리플레이가 결정적이다.

## 룬 실패 진단 (추측 금지 — 증거 먼저)

실행 증거는 설치 폴더 `logs\`에 남는다(시도마다 갱신): `rune-frame-0..3.png`(판정 버스트),
`rune-before.png`(발동 직전 기준), `rune-strip-NN.png`(밴드 50ms 연속 녹화), `rune-solve.txt`
(판정 트레이스), `rune-input.png`(입력 직전), `rune-minimap.png`. 고정 이름은 다음 시도가
덮어쓰지만, **실패(인식 실패·퍼즐 잔존) 시점의 사본이 `rune-fail-일시\` 폴더에, 입력까지 간
모든 시도는 `rune-attempt-일시\` 폴더에 자동 보존**되고 실패/완료 앱로그가 해당 폴더명을 직접
안내한다(14일 뒤 자동 정리).

### 룬 로그 읽는 법 (2026-08-05 개편 — 문구가 곧 사실이 되게 전면 정리)

- 앱로그(`app-날짜.log`): 룬 흐름은 `위치보정:rune`으로 그렙. "퍼즐 관찰 시작 … 판정 t=0"
  라인의 시각이 rune-solve.txt의 t=0 기준. 위치 확보는 반드시 "위치 확보/교체/보정(경로)"
  형태로 한 줄 남는다 — 경로 표기는 ④ 줄 채택 / ④→⑦w 교체 / ④+⑦w 보정 / ⑦ 부분 줄 /
  ⑦w 웜톤 줄 / ⑧ 소스 융합 (+재선출·재배치). 실패는 warn이라 `error-날짜.log`에도 복사됨.
- "확정 지연 — … 연장(회전 관측과 무관)"은 예산 연장일 뿐 회전 아님. 회전의 진실은
  "회전 관측 — 각도 진행 감지" 라인과 rune-solve.txt 헤더의 `회전관측=True`뿐.
- "룬 사용 완료"는 퍼즐 닫힘 확인일 뿐 정답 보증 아님(오답도 닫힘) — 정답 검증은 로그가
  안내한 rune-attempt 폴더의 rune-input.png·rune-solve.txt로.
- rune-solve.txt: 슬롯 번호는 좌→우 1기반(앱로그와 동일). `lock=U@1293ms`=잠금 방향·시각,
  반동표는 회전 전용이라 정지형은 전부 0이 정상. 각도열은 주축각 참고용 — 방향 분류와 별개
  (침식 글리프는 주축이 틀리면서 방향은 맞을 수 있음, 방향-주축 게이트 기각 실측 2026-08-05).
  파일 말미 범례 2줄이 요약. 이 파일은 아무 도구도 파싱하지 않는다(자유 수정 가능).
- analysis.txt(오프라인): 진단 라인은 `[단계|…]` 접두로 소속 명시(예: `[②|밴드 …]`).
  ⑤⑥은 실전 미사용, ⓪ "선택된 줄"은 진단 픽 — 실전 판정은 ④. 로컬 관찰 섹션은 ⑨
  (구 "⑦ 위치" — ⑦ 부분 줄과 번호 충돌로 개명). 그라데이션 보정 진단은 인자 선평가 탓에
  소속 밴드 라인보다 위에 찍힌다. 출력 라인 포맷은 expected.txt 계약 — 바꾸면 픽스처 동시 이행.

```powershell
# 퍼즐 인식 재현(프레임): 실전 경로는 ④ — 줄 채택 위치·방향이 여기서 나온다.
# 위치 폴백 사슬(위치만, 방향은 로컬 관찰): ⑦ 부분 줄 → ⑦w 웜톤 줄 → ⑧ 소스 융합.
# 실전에서 융합이 발동했으면 rune-solve.txt 말미에 "융합: …" 라인이 남는다 → 그 케이스는
# 픽스처로 보존할 것. 첫 발동(2026-08-05 09:20)은 중심 게이트 부재로 오답 입력을 냈고
# LLUL-coolglow 픽스처가 그 사건이다. 입력까지 간 시도는 rune-attempt-일시\로 전부 보존됨
# (오답 입력은 퍼즐이 닫혀 rune-fail 스냅샷이 안 남는다 — attempt 폴더가 유일한 증거).
YInput.exe --rune-analyze rune-frame-0.png rune-frame-1.png rune-frame-2.png rune-frame-3.png rune-before.png

# 회전 반동 재현(스트립): 파일명에 rune-strip 포함 시 스트립 모드. pos=로 관찰좌표 고정 가능
YInput.exe --rune-analyze rune-before.png rune-strip-*.png
YInput.exe --rune-analyze rune-before.png rune-strip-*.png pos=442,240;550,244;639,240;729,242
# 주의: 스트립 모드의 위치 획득은 분석기 전용 폴백 — '줄 채택' 검증은 프레임(④)으로 할 것

# 미니맵 룬 아이콘 오탐/미탐 재현
YInput.exe --rune-minimap-analyze rune-minimap.png    # → .rune.txt 후보·탈락사유·평균색
```

## 회귀 검증 (인식 로직 수정 시 필수)

**한 명령으로 전 세트 자동 대조** — 룬 코드를 고쳤으면 무조건 이걸 돌린다(종료코드=실패 수):

```powershell
powershell -File tools\rune-regress.ps1        # 빌드 + 17세트 리플레이 + expected.txt 대조
powershell -File tools\rune-regress.ps1 -NoBuild -Set *DDRD*   # 특정 세트만
```

세트별 기대값은 `tests\fixtures\<세트>\expected.txt`(지시자: run/out/re/forbid/dirs/xs/rowxs).
새 실패 픽스처를 추가하면 expected.txt도 함께 만든다. 아래 표는 사람용 요약:

| 세트 | 종류 | 기대 |
|---|---|---|
| `rune-strips-LRRU-fail4` | 회전2+정지2 | L R R U |
| `rune-strips-DDRD-20260804` | 회전2+정지2 | 2:D 3:R 4:D (1 미확정 허용) |
| `rune-frames-DLUU-purple-20260804` | 고전 정지형 | ⑦ 부분줄 455/548/633/735 ±5 |
| `rune-strips-LUUX-slotshift-20260804` | 슬롯 어긋남 | 줄 채택 442/550/639/729 ±6 (프레임으로) |
| `rune-frames-UUUR-carcion-20260804` | 정지형·몹 점거 | ④ ↑↑↑→ · 위치 358/470/537/646 ±5 |
| `rune-frames-DULR-carcion2-20260804` | 정지형·불균일 간격(비 2.78) | ④ ↓↑←→ · 위치 389/486/622/671 ±5 |
| `rune-frames-DRDD-purple4-20260804` | 침식+병합 공존·에지 잡블롭 | ②③ 에지 보정 789→711 · 줄 439/531/628/711 ±5 |
| `rune-frames-RRDD-effectmerge-20260804` | 이펙트 병합 잡줄·오답 입력 | ② 줄 475/598/682/749 ±5 (라이브: 잠금보호·중복관측·충돌보류) |
| `rune-strips-DDLR-fliplock-20260804` | 회전2·머리 플립 고착 | 1:D 2:D 3:L 4:R (pos= 고정, 리셋 발생) |
| `rune-frames-DLRD-pilloffset-20260804` | 정지형·필 중심 이탈 67px | ②③ 줄 492/608/697/771 ±5 (④ 실패 허용) |
| `rune-frames-DDLR-pillhigh-20260804` | 정지형·필 상단 이탈(밴드 8.9%) | ④ ↓↓←→ · 위치 411/498/559/650 ±5 |
| `rune-frames-LLDR-erosion-20260804` | 정지형·침식 소실+밀린 줄 | ⓪ 파편 419/a30 · ⑨ 로컬 관찰 481/582/663/765 (라이브: 슬롯 재배치) |
| `rune-frames-LLUL-coolglow-20260805` | 한색 광류 병합·융합 첫 오답 | ④·⑦ 실패 · ⑦w 웜톤 줄 505/592/654/780 ±5 · ② ←←↑← |
| `rune-frames-UUDL-coolglow2-20260805` | ④ 잡줄 채택·웜 교차 검증 근거 | ⑦w 396/500/595/689 ±5 · 스트립(pos=) 4/4 ↑↑↓← (침식 주축 회귀 감시) |
| `rune-frames-LUUR-coolglow3-20260805` | ④ 1슬롯 잡 교체·웜 슬롯 보정 근거 | ⑦w 458/588/674/730 ±5 · ② L U U R |
| `rune-frames-LDUU-rowshift-20260805` | ④ 잡줄 채택(중심 68px 이탈)·웜 균일성 교체 근거 | ⑦w·⑧ 459/558/654/733 ±5 · ② L D U U |
| `rune-minimap\` | 아이콘 3장 | README 참조 |

리플레이 산출물(`*.analysis.txt`, `*.png.mask-*.png`)은 gitignore됨 — 커밋하지 말 것.

## 커밋·푸시

- 모든 변경은 main에 커밋 후 즉시 푸시. 커밋 메시지는 한국어, 제목 1줄 + 본문(원인·수정·검증).
- 여러 줄 메시지는 파일로: `git commit -F commitmsg.txt` (PowerShell here-string은 BOM이 붙음 — 파일로 쓸 것)
- 트레일러: `Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>`

## 동작 원칙 (사용자 지정)

- 고정 대기 금지 — 상태 폴링으로 즉시 진행 (예: 종료 대기, 착지 판정, 서버 기동)
- 요청이 조금이라도 애매하면 추측하지 말고 먼저 확인
- 룬 정답은 **사용자 관측이 항상 기준** — 저장 프레임으로 반박하지 않는다
- **룬 인식 관련 코드는 수정 전에 실패 원인 분석을 사용자에게 보고하고 검수받은 뒤에만 수정** (2026-08-04 지정 — 사용자가 직접 지시한 수정은 바로 진행)
- **룬 발동은 재시도·재발동 없음**: 스페이스 1회 → 열렸다고 전제 → 인식 1회 → 실패면 오류 후 종료. 열린 퍼즐에 스페이스 = 오답 입력이라 재발동 금지. 넉백 복귀 로직도 두지 않음(캐릭터는 밀려나지 않음)
- 인식 게이트/상수 수정 시 근거 실측값을 주석에 남기고, 회귀 세트 전부 재검증
- **로그·주석에 이모지 등 꾸밈요소 금지** (2026-08-05 지정) — 1차 독자는 Claude. 사실만 간결하게, 문구가 곧 동작 사실이어야 한다(예: "회전 감지"라 쓰려면 실제 회전 관측이어야 함)
