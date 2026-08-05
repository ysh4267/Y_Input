using System.Drawing;
using System.Drawing.Imaging;

namespace YInput.Host.Vision;

/// <summary>오프라인 진단 CLI(--rune-analyze) — 저장된 프레임/스트립으로 인식·판정을 재현(partial).
/// 스트립 모드는 실전과 같은 RunePuzzleSolver를 구동한다(미러 없음).</summary>
internal static partial class RuneArrowDetector
{
    /// <summary>진단 CLI(--rune-analyze) — 저장된 퍼즐 스크린샷으로 인식 과정을 재현해
    /// 첫 파일 경로 + ".analysis.txt"로 남긴다. 파일 1개 = 채도 마스크만(크롭 검증),
    /// 여러 개(rune-frame-N 연속 캡처) = 실전과 같은 애니메이션 차분 합집합.
    /// 스트립 모드에서 "pos=x,y;x,y;x,y;x,y" 인자를 주면 위치 획득을 건너뛰고 그 좌표를
    /// 관찰한다(프레임 절대 y는 밴드 기준으로 자동 변환) — 반동 감지 단독 검증용.</summary>
    public static void AnalyzeToFile(params string[] pngPaths)
    {
        // 스트립(rune-strip-NN) 입력이면 회전 반동 파형 재현 모드로 — 각 스트립의 화살표별
        // 로컬 방향·각도 시계열과, 실전 반동 감지가 각 표본에서 내렸을 판정을 그대로 찍는다.
        if (pngPaths.Any(p => Path.GetFileName(p).Contains("rune-strip", StringComparison.OrdinalIgnoreCase)))
        {
            AnalyzeStripsToFile(pngPaths);
            return;
        }
        var sb = new System.Text.StringBuilder();
        var frames = new List<Bitmap>();
        Bitmap? beforeRef = null;
        try
        {
            // 'rune-before'가 포함된 파일은 발동 전 기준 프레임으로 사용(실전 경로 재현)
            foreach (var p in pngPaths)
            {
                if (Path.GetFileName(p).Contains("rune-before", StringComparison.OrdinalIgnoreCase))
                    beforeRef = new Bitmap(p);
                else frames.Add(new Bitmap(p));
            }
            // 크기가 다른 프레임(다른 실행의 잔재)은 차분에 못 쓴다 — 첫 프레임 크기 기준으로 필터
            int skipped = frames.RemoveAll(f => f.Width != frames[0].Width || f.Height != frames[0].Height);
            if (skipped > 0) sb.AppendLine($"크기 불일치 프레임 {skipped}개 제외(기준: 첫 프레임 {frames[0].Width}x{frames[0].Height} — 첫 프레임이 낡은 실행의 잔재면 기준 자체가 문제일 수 있음)");
            var frame = frames[^1];
            // 작은 이미지(퍼즐 영역 크롭·화살표 크롭)는 전체를 분석, 전체 창 캡처는 비율 영역 적용
            var region = frame.Width < 700 || frame.Height < 500
                ? new Rectangle(0, 0, frame.Width, frame.Height)
                : default;
            if (region == default && !TryRegion(frame, false, out region)) { sb.AppendLine("영역 계산 실패"); }
            else
            {
                int w = region.Width, h = region.Height;
                var mask = VividMask(frame, region, w, h);
                if (frames.Count >= 2)
                {
                    var changed = new bool[w * h];
                    for (int k = 1; k < frames.Count; k++)
                        AccumulateDiff(frames[k - 1], frames[k], region, w, h, AnimDiffMin, changed);
                    for (int i = 0; i < mask.Length; i++) mask[i] &= changed[i];
                }
                SaveMaskPng(mask, w, h, pngPaths[0] + ".mask-pre.png");
                if (frames.Count == 1) ThinFilter(mask, w, h); // 실전과 동일: 애니메이션 경로는 두께 필터 없음
                SaveMaskPng(mask, w, h, pngPaths[0] + ".mask-post.png");
                sb.AppendLine($"frame {frame.Width}x{frame.Height} region {region} 입력 {frames.Count}프레임"
                              + (frames.Count >= 2 ? " (애니메이션 차분 합집합)" : " (채도 마스크만)"));

                var merged = MergeNear(FindBlobs(mask, w, h));
                sb.AppendLine($"병합 블롭 {merged.Count}개 (면적순 상위 30, 좌표는 프레임 절대):");
                foreach (var b in merged.OrderByDescending(x => x.Area).Take(30))
                {
                    bool sizeOk = b.Area is >= MinArrowArea and <= MaxArrowArea
                                  && b.W is >= MinArrowBox and <= MaxArrowBox && b.H is >= MinArrowBox and <= MaxArrowBox;
                    sb.AppendLine($"  ({region.X + b.Cx:0},{region.Y + b.Cy:0}) a{b.Area} {b.W}x{b.H}{(sizeOk ? " [후보]" : "")}");
                }

                var cands = merged.Where(b =>
                    b.Area is >= MinArrowArea and <= MaxArrowArea &&
                    b.W is >= MinArrowBox and <= MaxArrowBox && b.H is >= MinArrowBox and <= MaxArrowBox).ToList();
                sb.AppendLine($"후보 {cands.Count}개");
                var row = PickRow(cands, -1, frame.Width);
                if (row is null) sb.AppendLine("선택된 줄 없음(조건 만족 조합 없음 — ⓪ 진단 픽·중심 게이트 꺼짐, 실전 판정은 ④)");
                else
                {
                    sb.AppendLine("선택된 줄(⓪ 진단 픽 — 실전 판정은 ④, 방향은 그라데이션 보정 없음):");
                    foreach (var b in row)
                    {
                        var (dir, up, down, left, right) = ClassifyScores(b, w);
                        sb.AppendLine($"  ({region.X + b.Cx:0},{region.Y + b.Cy:0}) a{b.Area} {b.W}x{b.H} → {dir}  U{up:0.000} D{down:0.000} L{left:0.000} R{right:0.000}");
                    }
                }

                // 발동 전 프레임이 있으면 실전 3경로(애니메이션/발동 전 차분/채도 단독+배너)를 그대로 재현
                if (beforeRef is not null)
                {
                    bool pre = frame.Width < 700 || frame.Height < 500;
                    // 행별 배너 신호 프로파일 — '넓게 변한' 행 연속 구간과 어두워짐 정도
                    if (beforeRef.Width == frame.Width && beforeRef.Height == frame.Height)
                    {
                        var (dRows, lb, ln, sdBefore, sdNow) = RowStats(beforeRef, frame, region, DiffMin);
                        int wideThr = (int)(w * BannerWideFrac);
                        sb.AppendLine($"— 행별 배너 신호 (폭변화 ≥{wideThr}px + 어두워짐 ≥8 구간, 평탄화=표준편차 전→후) —");
                        for (int y = 0; y < h;)
                        {
                            if (dRows[y] < wideThr || lb[y] - ln[y] < 8) { y++; continue; }
                            int y0 = y; double dkSum = 0, dkMax = 0, sdbSum = 0, sdnSum = 0;
                            while (y < h && dRows[y] >= wideThr && lb[y] - ln[y] >= 8)
                            {
                                double dk = lb[y] - ln[y];
                                dkSum += dk; dkMax = Math.Max(dkMax, dk);
                                sdbSum += sdBefore[y]; sdnSum += sdNow[y]; y++;
                            }
                            int n2 = y - y0;
                            sb.AppendLine($"  y {region.Y + y0}..{region.Y + y - 1} ({n2}행) 어두워짐 평균 {dkSum / n2:0.0} 최대 {dkMax:0.0} 표준편차 {sdbSum / n2:0.0}→{sdnSum / n2:0.0}");
                        }
                    }
                    string Dirs(List<RuneArrow>? a) => a is null ? "실패"
                        : string.Join(" ", a.Select(x => x.Dir switch { 'L' => '←', 'R' => '→', 'U' => '↑', _ => '↓' }));
                    // ②경로 마스크(채도+발동 전 차분, 두께 필터 후)도 덤프 — 블롭 오염 원인 확인용
                    {
                        var m2 = VividMask(frame, region, w, h);
                        var ch2 = new bool[w * h];
                        AccumulateDiff(beforeRef, frame, region, w, h, DiffMin, ch2);
                        for (int i = 0; i < m2.Length; i++) m2[i] &= ch2[i];
                        ThinFilter(m2, w, h);
                        SaveMaskPng(m2, w, h, pngPaths[0] + ".mask-diffbefore.png");
                    }
                    if (frames.Count >= 2)
                        sb.AppendLine($"배너 텍스트 신호 = {PresentPixelCount(frames[^2], frames[^1], beforeRef, pre)}px (임계 {PresentMinPx} — PuzzlePresent)");
                    var band = BannerBand(w, h, FullFrameH(frame, pre));
                    sb.AppendLine($"— 실전 경로 재현 (배너 밴드 y {region.Y + band.Y0}..{region.Y + band.Y1}, 중심X {(band.CenterX >= 0 ? (region.X + band.CenterX).ToString("0") : "미탐지")}) —");
                    // 진단 단계 태그(2026-08-05 로그 개편) — 모든 진단([밴드/후보/에지 보정/
                    // 그라데이션/로컬])에 소속 단계(①~⑨)를 접두로 박는다. 과거엔 전부 같은 래퍼라
                    // 어느 단계 것인지 구분 불가였고, ①~③은 라벨 보간 구멍 안에서 검출기를 호출해
                    // 진단이 라벨 라인 '안에' 끼어드는 인터리브까지 있었다(rowxs가 그 기형에 의존) —
                    // 호출을 라벨 밖 별도 문장으로 빼 제거(픽스처 rowxs 앵커는 "②|밴드"로 동시 이행).
                    // (주의) 그라데이션 보정 진단은 인자 선평가 탓에 소속 밴드 라인보다 위에 찍힌다.
                    string stage = "";
                    DiagLog = s => sb.AppendLine($"      [{stage}{s}]");
                    try
                    {
                        stage = "①|";
                        var r1 = frames.Count >= 2 ? FindArrowsAnimated(frames, beforeRef, pre) : null;
                        sb.AppendLine($"  ① 애니메이션 차분: {(frames.Count >= 2 ? Dirs(r1) : "프레임 부족")}");
                        stage = "②|";
                        var r2 = FindArrows(frame, beforeRef, beforeRef, pre);
                        sb.AppendLine($"  ② 발동 전 차분:   {Dirs(r2)}");
                        stage = "③|";
                        var r3 = FindArrows(frame, null, beforeRef, pre);
                        sb.AppendLine($"  ③ 채도 단독:      {Dirs(r3)}");
                        var fusePool = new FusionPool(); // ④와 같은 호출에서 수집 — 실전(StepFrame)과 동일 경로
                        stage = "④|";
                        var af = AnalyzeFrame(frame, frames.GetRange(0, frames.Count - 1), beforeRef, pre, fusePool);
                        sb.AppendLine($"  ④ 프레임 분석(교집합→정지 게이트, 실전 경로): {(af is null ? "실패" : string.Join(" ", af.Select(x => x.Dir switch { 'L' => '←', 'R' => '→', 'U' => '↑', _ => '↓' })))}");
                        stage = "⑦|";
                        var pr = TryPartialRow(frame, beforeRef, pre);
                        sb.AppendLine($"  ⑦ 부분 줄 보완(3개+외삽): {(pr is null ? "실패" : string.Join(" ", pr.Select(p => $"({p.X:0},{p.Y:0})")))}");
                        // ⑦w 웜톤 줄(위치 전용) — 실전에서는 ⑦ 실패 시 다음 폴백. 풀에 웜 후보도 기여.
                        stage = "⑦w|";
                        var wr = TryWarmRow(frame, beforeRef, pre, fusePool, out _, out var wDirs);
                        sb.AppendLine($"  ⑦w 웜톤 줄(위치만): {(wr is null ? "실패" : string.Join(" ", wr.Select(p => $"({p.X:0},{p.Y:0})")))}");
                        // 합의 잠금 캘리브레이션용 — 웜 방향·마진(문자만 사용: 화살표·괄호숫자 없음 → 지시자 계약 안전)
                        if (wDirs is not null)
                            sb.AppendLine($"      [⑦w|방향·마진 {string.Join(" ", wDirs.Select(d => $"{d.Dir}{d.Margin:0.00}"))}]");
                        // 방향 협응 캘리브레이션(2026-08-05 16:33) — 웜 줄 각 위치의 로컬(sat체인) 판독 대조.
                        // 실전 교차 검증 제3 판별자(웜·로컬 ≥3 일치 → 교체)의 코퍼스 검증 창구.
                        if (wr is not null && wDirs is not null)
                        {
                            var wpa = wr.Select(p => AnalyzeArrowAt(frame, beforeRef, null,
                                new Rectangle((int)(p.X - RunePuzzleSolver.PosBox / 2.0), (int)(p.Y - RunePuzzleSolver.PosBox / 2.0),
                                              RunePuzzleSolver.PosBox, RunePuzzleSolver.PosBox))).ToArray();
                            int wAgree = Enumerable.Range(0, 4).Count(i => wpa[i] is { } q && q.Area >= 60 && q.Dir == wDirs[i].Dir);
                            int wMm = af is null ? -1 : Enumerable.Range(0, 4).Count(i => Math.Abs(wr[i].X - af[i].Center.X) > RunePuzzleSolver.MinSlotSepPx);
                            sb.AppendLine($"      [⑦w|로컬협응 {string.Join(" ", wpa.Select(q => q is { } x ? $"{x.Dir}a{x.Area}" : "무"))} 일치 {wAgree}/4 ④불일치 {(wMm < 0 ? "④없음" : wMm + "슬롯")}]");
                        }
                        // ⑧ 소스 융합 — 실전에서는 ④·⑦·⑦w가 모두 실패했을 때만 발동하는 최후 폴백.
                        // 여기서는 항상 찍어 융합 풀·게이트 동작을 관찰한다(캘리브레이션·회귀 창구).
                        sb.AppendLine($"      [{fusePool.Stats()}]");
                        stage = "⑧|";
                        var fused = TryFusedRow(fusePool, frame, beforeRef, out var fusedContrib);
                        sb.AppendLine($"  ⑧ 소스 융합: {(fused is null ? "실패" : string.Join(" ", fused.Select(p => $"({p.X:0},{p.Y:0})")))}");
                        sb.AppendLine($"      [융합 기여 {fusedContrib}]");
                        // ⑤ 실험: 채도 교집합 — 모든 프레임에서 '계속 채도 높음'(정적 UI) && 발동 전과 다름.
                        //    흔들리는 불꽃은 프레임마다 위치가 바뀌어 교집합에서 탈락한다.
                        if (frames.Count >= 2)
                        {
                            foreach (int sat in new[] { VividSat, 80 })
                            {
                                var mi = VividMask(frames[0], region, w, h, sat, requireWarm: false);
                                for (int k = 1; k < frames.Count; k++)
                                {
                                    var vk = VividMask(frames[k], region, w, h, sat, requireWarm: false);
                                    for (int i = 0; i < mi.Length; i++) mi[i] &= vk[i];
                                }
                                var chI = new bool[w * h];
                                AccumulateDiff(beforeRef, frame, region, w, h, DiffMin, chI);
                                for (int i = 0; i < mi.Length; i++) mi[i] &= chI[i];
                                ThinFilter(mi, w, h);
                                SaveMaskPng(mi, w, h, pngPaths[0] + $".mask-vivid-and-{sat}.png");
                                stage = "⑤|";
                                var r5 = DetectRow(frame, beforeRef, mi, region, w, h, thinFilter: false, FullFrameH(frame, pre));
                                sb.AppendLine($"  ⑤ 채도 교집합(sat{sat}·실전 미사용·그라데이션 보정 없음): {(r5 is null ? "실패" : string.Join(" ", r5.Select(b => ClassifyScores(b, w).Dir switch { 'L' => '←', 'R' => '→', 'U' => '↑', _ => '↓' })))}");
                                // ⑥ 같은 마스크를 배너 밴드 없이 전 영역에서 — 밴드 기하가 틀리는 창 크기 대비
                                stage = "⑥|";
                                var r6 = DetectRow(frame, beforeRef, mi, region, w, h, thinFilter: false, FullFrameH(frame, pre), fullArea: true);
                                sb.AppendLine($"  ⑥ 교집합 전영역(sat{sat}·실전 미사용·그라데이션 보정 없음): {(r6 is null ? "실패" : string.Join(" ", r6.Select(b => ClassifyScores(b, w).Dir switch { 'L' => '←', 'R' => '→', 'U' => '↑', _ => '↓' })))}");
                            }
                            // ⑨ ④의 줄 위치를 기준으로 프레임별 로컬 방향·각도 — 회전 변형(반동 추적) 재현.
                            // 구 라벨 "⑦ 위치"는 위의 '⑦ 부분 줄 보완'과 번호가 충돌해 개명(2026-08-05,
                            // 픽스처 xs/re 앵커도 동시 이행 — DLUU의 ④ 성공/실패 계약 감시 라인).
                            stage = "⑨|";
                            if (af is null) sb.AppendLine("  ⑨ 로컬 관찰 — 위치 없음(④ 줄 실패)");
                            else
                            {
                                sb.AppendLine("  ⑨ 로컬 관찰 위치(④ 줄 기준): " + string.Join(" ", af.Select(p => $"({p.Center.X:0},{p.Center.Y:0})")));
                                const int box7 = 64;
                                for (int fi = 0; fi < frames.Count; fi++)
                                {
                                    var parts = new List<string>();
                                    foreach (var p in af)
                                    {
                                        var rect = new Rectangle((int)(p.Center.X - box7 / 2.0), (int)(p.Center.Y - box7 / 2.0), box7, box7);
                                        var la = AnalyzeArrowAt(frames[fi], beforeRef, fi > 0 ? frames[fi - 1] : null, rect);
                                        parts.Add(la is { } a
                                            ? $"{(a.MovingPx >= 40 ? "회" : "정")}{(a.Dir switch { 'L' => '←', 'R' => '→', 'U' => '↑', _ => '↓' })}{a.AngleDeg:000}°a{a.Area}M{a.Margin:0.00}"
                                            : "×");
                                    }
                                    sb.AppendLine($"     f{fi}: {string.Join("  ", parts)}");
                                }
                            }
                        }
                    }
                    finally { DiagLog = null; }
                }
            }
        }
        catch (Exception ex) { sb.AppendLine("오류: " + ex); }
        finally { foreach (var f in frames) f.Dispose(); }
        File.WriteAllText(pngPaths[0] + ".analysis.txt", sb.ToString());
    }

    /// <summary>스트립 녹화(밴드 크롭, ~50ms 간격) 재현 — 위치를 잡고 스트립마다 화살표별
    /// 로컬 방향·각도를 찍으며, 실전 반동 감지(RuneAngleTracker.TryDetectRecoil)를 그대로 돌려
    /// 어느 표본에서 락이 걸렸을지 재현한다. 출력: 첫 스트립 경로 + ".analysis.txt".</summary>
    private static void AnalyzeStripsToFile(string[] pngPaths)
    {
        var sb = new System.Text.StringBuilder();
        Bitmap? before = null, beforeStrip = null;
        var strips = new List<(string Name, Bitmap Bmp)>();
        PointF[]? fixedPos = null; // "pos=x,y;…" 인자 — 위치 획득 생략, 반동 감지 단독 검증
        try
        {
            foreach (var p in pngPaths)
            {
                if (p.StartsWith("pos=", StringComparison.OrdinalIgnoreCase))
                {
                    fixedPos = p[4..].Split(';', StringSplitOptions.RemoveEmptyEntries)
                        .Select(t => t.Split(','))
                        .Select(a => new PointF(float.Parse(a[0]), float.Parse(a[1]))).ToArray();
                    continue;
                }
                if (Path.GetFileName(p).Contains("rune-before", StringComparison.OrdinalIgnoreCase)) before = new Bitmap(p);
                else strips.Add((Path.GetFileName(p), new Bitmap(p)));
            }
            if (before is null) { sb.AppendLine("rune-before가 필요합니다(밴드 기준 크롭용)"); return; }
            var bandRect = ArrowBandRect(before.Width, before.Height);
            beforeStrip = before.Clone(bandRect, before.PixelFormat);
            int skipped = strips.RemoveAll(s => s.Bmp.Width != beforeStrip.Width || s.Bmp.Height != beforeStrip.Height);
            if (skipped > 0) sb.AppendLine($"크기 불일치 스트립 {skipped}장 제외(기준: rune-before 밴드 {beforeStrip.Width}x{beforeStrip.Height} — before가 다른 실행 것이면 정상 스트립이 전부 제외된다)");
            if (strips.Count == 0) { sb.AppendLine("스트립 없음"); return; }
            int w = beforeStrip.Width, h = beforeStrip.Height;
            var region = new Rectangle(0, 0, w, h);
            sb.AppendLine($"스트립 {strips.Count}장 ({w}x{h}), 기준 밴드 {bandRect}");

            // 위치 획득 — 기준 후보를 차례로 시도: ① rune-before의 밴드 크롭, ② 첫 스트립
            // (녹화 초반은 퍼즐이 뜨기 전이라 같은 카메라의 깨끗한 기준이 된다 — rune-before는
            // 시도 사이 카메라 밀림으로 낡을 수 있다). 차분 없이 채도 단독은 배경 장식(수정 등)이
            // 홍수를 일으켜 회전 화살표를 삼키므로 쓰지 않는다.
            PointF[]? pos = null;
            Bitmap activeRef = beforeStrip;
            if (fixedPos is { Length: 4 })
            {
                // 프레임 절대 y(밴드 높이 이상)는 밴드 기준으로 변환
                pos = fixedPos.Select(p => new PointF(p.X - bandRect.X,
                    p.Y >= h ? p.Y - bandRect.Y : p.Y)).ToArray();
                sb.AppendLine("위치(pos= 지정): " + string.Join(" ", pos.Select(p => $"({p.X:0},{p.Y:0})")));
            }
            foreach (var (refName, refBmp) in new (string, Bitmap)[] { ("before", beforeStrip), ("strip0", strips[0].Bmp) })
            {
                for (int si = 0; si < strips.Count && pos is null; si++)
                {
                    var (name, s) = strips[si];
                    if (ReferenceEquals(s, refBmp)) continue;
                    // 고채도(80) ∧ 기준 차분, 두께 필터 없음 — 회전형 외곽선 글리프를 살리면서
                    // 배경 장식(수정 등)은 차분이, 필 테두리 링은 박스 크기 제한이 걸러준다.
                    // 채도를 45로 낮추면 필 내부의 어두워진 하늘(sat≈70)까지 통과해 밴드 전체가
                    // 홍수가 된다(2026-08-04 마스크 덤프 확인) — 45 금지.
                    var mask = VividMask(s, region, w, h, VividSatStrict, requireWarm: false);
                    var fresh = new bool[w * h];
                    AccumulateDiff(refBmp, s, region, w, h, DiffMin, fresh);
                    for (int i = 0; i < mask.Length; i++) mask[i] &= fresh[i];
                    if (si == 45) SaveMaskPng((bool[])mask.Clone(), w, h, pngPaths[0] + $".mask-{refName}-s45.png");
                    DiagLog = si is < 2 or (>= 44 and < 47) ? line => sb.AppendLine($"  [{refName}/{name}] {line}") : null;
                    List<Blob>? row;
                    try { row = DetectRow(s, refBmp, mask, region, w, h, thinFilter: false, h, fullArea: true); }
                    finally { DiagLog = null; }
                    // 화살표 줄은 밴드 세로 중앙 부근 + 필 가로 중앙 대칭(실측 이탈 ≤5px, 0.05W) —
                    // 가장자리 잡블롭 줄·한 칸 밀린 줄(2026-08-04: x22~191 줄, x287 밀림 줄)은 기각
                    if (row is not null && (Math.Abs(row.Average(b => b.Cy) - h / 2.0) > h * 0.2
                                            || Math.Abs(row.Average(b => b.Cx) - w / 2.0) > w * 0.05))
                    {
                        sb.AppendLine($"  [{refName}/{name}] 줄 기각(중앙 이탈): "
                                      + string.Join(" ", row.Select(b => $"({b.Cx:0},{b.Cy:0})")));
                        row = null;
                    }
                    if (row is not null)
                    {
                        pos = row.Select(b => new PointF((float)b.Cx, (float)b.Cy)).ToArray();
                        activeRef = refBmp;
                        sb.AppendLine($"위치({refName}/{name}): " + string.Join(" ", pos.Select(p => $"({p.X:0},{p.Y:0})")));
                    }
                }
                if (pos is not null) break;
            }
            if (pos is null)
            {
                // 폴백: 부분 줄로 4슬롯 격자 추정 — 회전 글리프가 배경 홍수(수정 반짝임 등)에 묻혀
                // 줄 4개가 안 채워지는 맵 대비. 같은 y(±10)에서 간격 70~110의 이웃 쌍을 찾고,
                // 격자 중심이 밴드 중앙(≈필 중앙)에 가장 가까운 배치를 택한다. EMA가 잔차를 수렴시킨다.
                for (int si = 10; si < strips.Count && pos is null; si += 5)
                {
                    var (name, s) = strips[si];
                    var mask = VividMask(s, region, w, h, VividSatStrict, requireWarm: false);
                    var fresh = new bool[w * h];
                    AccumulateDiff(strips[0].Bmp, s, region, w, h, DiffMin, fresh);
                    for (int i = 0; i < mask.Length; i++) mask[i] &= fresh[i];
                    var cands = MergeNear(FindBlobs(mask, w, h))
                        .Where(x => x.Area is >= 100 and <= 1200 && x.W is >= 12 and <= 60 && x.H is >= 12 and <= 60)
                        .OrderBy(x => x.Cx).ToList();
                    for (int a1 = 0; a1 < cands.Count - 1 && pos is null; a1++)
                        for (int b1 = a1 + 1; b1 < cands.Count && pos is null; b1++)
                        {
                            double gap = cands[b1].Cx - cands[a1].Cx;
                            if (gap < 70 || gap > 110 || Math.Abs(cands[b1].Cy - cands[a1].Cy) > 10) continue;
                            double bestOff = double.MaxValue; PointF[]? bestLat = null;
                            for (int shift = 0; shift < 4; shift++)
                            {
                                double x0 = cands[a1].Cx - shift * gap;
                                if (x0 < 30 || x0 + 3 * gap > w - 30) continue;
                                double centerOff = Math.Abs(x0 + 1.5 * gap - w / 2.0);
                                if (centerOff < bestOff)
                                {
                                    bestOff = centerOff;
                                    bestLat = Enumerable.Range(0, 4)
                                        .Select(k => new PointF((float)(x0 + k * gap), (float)cands[a1].Cy)).ToArray();
                                }
                            }
                            if (bestLat is not null)
                            {
                                pos = bestLat;
                                activeRef = strips[0].Bmp;
                                sb.AppendLine($"위치(격자 추정 {name}, 쌍 ({cands[a1].Cx:0},{cands[a1].Cy:0})+({cands[b1].Cx:0},{cands[b1].Cy:0})): "
                                              + string.Join(" ", pos.Select(p => $"({p.X:0},{p.Y:0})")));
                            }
                        }
                }
            }
            if (pos is null) { sb.AppendLine("위치 획득 실패 — 어떤 스트립에서도 줄을 못 찾음"); return; }
            var posAnchor = (PointF[])pos.Clone(); // EMA 클램프 기준
            sb.AppendLine("  (범례) t=명목 50ms 간격(실전 시계보다 ~0.4초 이르다) · 회/정=회전 판정 · ★락=이 표본에서 잠금 · [확정X]=이미 잠금된 슬롯 · 여기 스트립 위치 획득은 분석기 전용 — 실전 줄 채택 검증은 프레임 ④로");

            // 실전과 같은 솔버를 스트립 시퀀스로 구동(2026-08-04 모듈화) — 이전의 LocalPass
            // 손복제 미러(~90줄)를 제거. 반동표·플립 리셋은 diag, 재선출·재배치 노트는 note로
            // 같은 위치에 찍힌다. 시각은 명목 50ms 간격 고정값(링 버퍼라 실전 t보다 ~0.4초 이르다
            // — TryRelocate 2200ms 게이트 등은 그 좌표계로 동작, 미러 시절과 동일).
            var solver = new RunePuzzleSolver(activeRef, initialBudgetMs: int.MaxValue, precropped: true,
                note: n => sb.AppendLine($"      [{n}]"),
                diag: m => sb.AppendLine($"      {m}"));
            solver.AdoptPositions(pos);
            for (int fi = 0; fi < strips.Count; fi++)
            {
                double t = fi * (double)RunePuzzleSolver.FastTickMs; // 명목 50ms 간격
                DiagLog = fi is 30 or 40 ? m => sb.AppendLine($"      [f{fi}] {m}") : null;
                DiagMaskDir = fi == 30 ? Path.GetDirectoryName(pngPaths[0]) : null;
                var rd = solver.StepLocal(strips[fi].Bmp, fi > 0 ? strips[fi - 1].Bmp : null, (long)t);
                DiagLog = null; DiagMaskDir = null;
                var parts = new List<string>();
                for (int j = 0; j < 4; j++)
                {
                    if (rd[j].WasLocked) { parts.Add($"[확정{rd[j].LockedDir}]"); continue; }
                    if (!rd[j].Analyzed) { parts.Add("×"); continue; }
                    parts.Add($"{(rd[j].RotActive ? "회" : "정")}{(rd[j].Dir switch { 'L' => '←', 'R' => '→', 'U' => '↑', _ => '↓' })}{rd[j].AngleDeg:000}°a{rd[j].Area}m{rd[j].MovingPx}M{rd[j].Margin:0.00}{(rd[j].NewlyLocked ? "★락" : "")}");
                }
                sb.AppendLine($"f{fi:00} {t,5:0}ms  {string.Join("  ", parts)}");
                solver.TryRelocate(strips[fi].Bmp, (long)t); // 3잠금+1잡 슬롯 재배치(실전 동일 로직)
            }
            sb.AppendLine("반동 투표 [R U L D]:");
            for (int j = 0; j < 4; j++)
                sb.AppendLine($"  화살표{j + 1}: {solver.GetVote(j, 0)} {solver.GetVote(j, 1)} {solver.GetVote(j, 2)} {solver.GetVote(j, 3)}  확정 {(solver.GetLocked(j) is { } dd ? dd.ToString() : "-")}  pos=({solver.GetPos(j).X:0},{solver.GetPos(j).Y:0})");
            var xOrder = Enumerable.Range(0, 4).OrderBy(j => solver.GetPos(j).X).ToList();
            sb.AppendLine("최종 입력(X순): " + string.Join(" ", xOrder.Select(j => solver.GetLocked(j) is { } d2
                ? (d2 switch { 'L' => "←", 'R' => "→", 'U' => "↑", _ => "↓" }) : "?")));
        }
        catch (Exception ex) { sb.AppendLine("오류: " + ex); }
        finally
        {
            before?.Dispose(); beforeStrip?.Dispose();
            foreach (var (_, b) in strips) b.Dispose();
            File.WriteAllText(pngPaths[0] + ".analysis.txt", sb.ToString());
        }
    }

    /// <summary>진단용 — 마스크를 흑백 PNG로 저장(어느 단계에서 화살표가 지워지는지 확인).</summary>
    private static void SaveMaskPng(bool[] mask, int w, int h, string path)
    {
        try
        {
            using var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try
            {
                unsafe
                {
                    for (int y = 0; y < h; y++)
                    {
                        byte* row = (byte*)data.Scan0 + y * data.Stride;
                        for (int x = 0; x < w; x++)
                        {
                            byte v = mask[y * w + x] ? (byte)255 : (byte)0;
                            row[x * 4] = v; row[x * 4 + 1] = v; row[x * 4 + 2] = v; row[x * 4 + 3] = 255;
                        }
                    }
                }
            }
            finally { bmp.UnlockBits(data); }
            bmp.Save(path, ImageFormat.Png);
        }
        catch { /* 진단 저장 실패 무시 */ }
    }
}
