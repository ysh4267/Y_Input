# Y_Input 작업 방법 (자주 쓰는 명령 모음)

드라이버 레벨 메이플 매크로. .NET 10, 웹 UI(127.0.0.1:48710) + 트레이 앱.
모든 명령은 PowerShell 기준. **SDK는 사용자 폴더 설치본**을 쓴다(PATH에 없음).

## 빌드·테스트

```powershell
$env:DOTNET_ROOT="$env:USERPROFILE\.dotnet"
& "$env:USERPROFILE\.dotnet\dotnet.exe" build "YInput.slnx"
& "$env:USERPROFILE\.dotnet\dotnet.exe" test  "YInput.slnx"    # 36개 전부 통과해야 함
```

## 게시 → 자동 설치 (한 사이클)

버전은 `0.3.NNN-local` 순번 증가(마지막 설치 버전 +1). 설치 경로: `%LOCALAPPDATA%\Programs\YInput\YInput.exe`.

```powershell
# 1) 게시 (publish/ 는 gitignore됨)
& "$env:USERPROFILE\.dotnet\dotnet.exe" publish "src\YInput.Host\YInput.Host.csproj" `
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

## 룬 실패 진단 (추측 금지 — 증거 먼저)

실행 증거는 설치 폴더 `logs\`에 남는다(시도마다 갱신): `rune-frame-0..3.png`(판정 버스트),
`rune-before.png`(발동 직전 기준), `rune-strip-NN.png`(밴드 50ms 연속 녹화), `rune-solve.txt`
(화살표별 잠금·반동 투표·각도 시계열), `rune-input.png`(입력 직전), `rune-minimap.png`,
`rune-close.png`(닫힘 대기 타임아웃 시). **새 실패가 나면 덮어쓰기 전에 먼저 통째로 백업할 것.**

```powershell
# 퍼즐 인식 재현(프레임): 실전 경로는 ④ — 줄 채택 위치·방향이 여기서 나온다
YInput.exe --rune-analyze rune-frame-0.png rune-frame-1.png rune-frame-2.png rune-frame-3.png rune-before.png

# 회전 반동 재현(스트립): 파일명에 rune-strip 포함 시 스트립 모드. pos=로 관찰좌표 고정 가능
YInput.exe --rune-analyze rune-before.png rune-strip-*.png
YInput.exe --rune-analyze rune-before.png rune-strip-*.png pos=442,240;550,244;639,240;729,242
# 주의: 스트립 모드의 위치 획득은 분석기 전용 폴백 — '줄 채택' 검증은 프레임(④)으로 할 것

# 미니맵 룬 아이콘 오탐/미탐 재현
YInput.exe --rune-minimap-analyze rune-minimap.png    # → .rune.txt 후보·탈락사유·평균색
```

## 회귀 검증 (인식 로직 수정 시 필수)

`tests\fixtures\` 의 세트 전부 리플레이해서 README 기대값과 대조:

| 세트 | 종류 | 기대 |
|---|---|---|
| `rune-strips-LRRU-fail4` | 회전2+정지2 | L R R U |
| `rune-strips-DDRD-20260804` | 회전2+정지2 | 2:D 3:R 4:D (1 미확정 허용) |
| `rune-frames-DLUU-purple-20260804` | 고전 정지형 | ⑦ 부분줄 455/548/633/735 ±5 |
| `rune-strips-LUUX-slotshift-20260804` | 슬롯 어긋남 | 줄 채택 442/550/639/729 ±6 (프레임으로) |
| `rune-frames-UUUR-carcion-20260804` | 정지형·몹 점거 | ④ ↑↑↑→ · 위치 358/470/537/646 ±5 |
| `rune-frames-DULR-carcion2-20260804` | 정지형·불균일 간격(비 2.78) | ④ ↓↑←→ · 위치 389/486/622/671 ±5 |
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
