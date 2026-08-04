using System.Drawing;
using System.Drawing.Imaging;

namespace YInput.Host.Vision;

/// <summary>미니맵에서 찾은 노란 점 후보 하나 — 중심(서브픽셀), 픽셀 수, 바운딩박스 크기.</summary>
internal readonly record struct DotCandidate(PointF Center, int Area, int BoxW, int BoxH);

/// <summary>
/// 미니맵에서 플레이어 노란 점을 찾는다. 노란 픽셀 전체의 평균이 아니라 <b>연결된 덩어리(블롭)</b>
/// 단위로 분리한 뒤 '점처럼 생긴' 블롭만 후보로 남긴다 — 미니맵에 다른 노란 요소(아이콘·장식)가
/// 있어도 그 사이 엉뚱한 위치로 계산되지 않는다. 여러 후보 중에서는 직전 위치에 가장 가까운 것
/// (추적) 또는 플레이어 점 크기(~3×3)에 가장 가까운 것을 고른다.
/// </summary>
internal static class MinimapDetector
{
    private const int MinBlobArea = 8;    // 글자·마커 파편(2~5px) 제외 — 플레이어 점은 3×3 이상
    private const int MaxBlobArea = 150;  // 큰 노란 UI 덩어리 제외
    private const int MaxBlobBox = 14;    // 점은 작다 — 넓게 퍼진 장식·텍스트 제외
    private const int TypicalDotArea = 24; // 플레이어 점 ≈ 지름 5~7px 원(스크린샷 기준)

    /// <summary>minimapRect 안의 점 후보 블롭 목록(중심은 minimapRect 상대, 서브픽셀).</summary>
    public static List<DotCandidate> FindDots(Bitmap frame, Rectangle minimapRect,
                                              int minR = 200, int minG = 180, int maxB = 120)
    {
        var list = new List<DotCandidate>();
        var area = Rectangle.Intersect(minimapRect, new Rectangle(0, 0, frame.Width, frame.Height));
        if (area.Width <= 0 || area.Height <= 0) return list;

        int w = area.Width, h = area.Height;
        var mask = new bool[w * h];
        var data = frame.LockBits(area, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            unsafe
            {
                for (int y = 0; y < h; y++)
                {
                    byte* row = (byte*)data.Scan0 + y * data.Stride;
                    int o = y * w;
                    for (int x = 0; x < w; x++)
                    {
                        byte b = row[x * 4], g = row[x * 4 + 1], r = row[x * 4 + 2];
                        mask[o + x] = r >= minR && g >= minG && b <= maxB;
                    }
                }
            }
        }
        finally { frame.UnlockBits(data); }

        // 연결 요소(4방향) 분리 — 블롭별 centroid/면적/바운딩박스
        var seen = new bool[w * h];
        var stack = new Stack<int>();
        float ox = area.X - minimapRect.X, oy = area.Y - minimapRect.Y;
        for (int i = 0; i < mask.Length; i++)
        {
            if (!mask[i] || seen[i]) continue;
            long sumX = 0, sumY = 0; int count = 0;
            int minX = w, maxX = -1, minY = h, maxY = -1;
            stack.Push(i); seen[i] = true;
            while (stack.Count > 0)
            {
                int p = stack.Pop();
                int px = p % w, py = p / w;
                sumX += px; sumY += py; count++;
                if (px < minX) minX = px; if (px > maxX) maxX = px;
                if (py < minY) minY = py; if (py > maxY) maxY = py;
                if (px > 0 && mask[p - 1] && !seen[p - 1]) { seen[p - 1] = true; stack.Push(p - 1); }
                if (px < w - 1 && mask[p + 1] && !seen[p + 1]) { seen[p + 1] = true; stack.Push(p + 1); }
                if (py > 0 && mask[p - w] && !seen[p - w]) { seen[p - w] = true; stack.Push(p - w); }
                if (py < h - 1 && mask[p + w] && !seen[p + w]) { seen[p + w] = true; stack.Push(p + w); }
            }
            int bw = maxX - minX + 1, bh = maxY - minY + 1;
            if (count < MinBlobArea || count > MaxBlobArea || bw > MaxBlobBox || bh > MaxBlobBox) continue;
            // 모양 필터: 플레이어 점은 '둥근 점' — 삼각형 마커(채움 낮음)·글자 조각(길쭉)을 배제
            double fill = (double)count / (bw * bh);        // 원형 ≈ 0.78, 삼각형 ≈ 0.5
            if (count >= 6 && fill < 0.55) continue;        // 아주 작은 점(수 px)은 채움 판정이 불안정 → 면제
            if (Math.Max(bw, bh) > 2 * Math.Min(bw, bh)) continue;
            list.Add(new DotCandidate(new PointF((float)sumX / count + ox, (float)sumY / count + oy), count, bw, bh));
        }
        return list;
    }

    /// <summary>후보 중 플레이어 점 선택 — near(직전 위치)가 있으면 가장 가까운 것(추적),
    /// 없으면 점 크기(~3×3)에 가장 가까운 것.</summary>
    public static DotCandidate Pick(List<DotCandidate> cands, PointF? near = null)
    {
        if (near is { } n)
            return cands.MinBy(c => (c.Center.X - n.X) * (c.Center.X - n.X) + (c.Center.Y - n.Y) * (c.Center.Y - n.Y));
        return cands.MinBy(c => Math.Abs(c.Area - TypicalDotArea));
    }

    /// <summary>플레이어 점 탐지(블롭 기반). dot은 minimapRect 상대, 서브픽셀.
    /// near = 직전 측정 위치(보정 중 추적) — 다른 노란 점으로 튀는 것을 막는다.</summary>
    public static bool TryFindPlayerDot(Bitmap frame, Rectangle minimapRect, out PointF dot,
                                        int minR = 200, int minG = 180, int maxB = 120, PointF? near = null)
    {
        dot = PointF.Empty;
        var cands = FindDots(frame, minimapRect, minR, minG, maxB);
        if (cands.Count == 0) return false;
        dot = Pick(cands, near).Center;
        return true;
    }

    // ---------- 룬(보라 다이아) 아이콘 탐지 ----------
    // 미니맵의 룬 아이콘은 보라·라벤더 계열 다이아 — B가 높고 G가 낮다. 파란 타 유저 점(R 낮음)·
    // 초록 포탈·노란 점·빨간 마커와 색으로 구분된다.
    private const int RuneMinBlobArea = 6;    // 다이아 코어만 잡혀도 인정
    private const int RuneMaxBlobArea = 300;
    private const int RuneMaxBlobBox = 24;
    // 색 게이트(블롭 평균) — 룬 다이아는 '마젠타'(실측 평균 RGB 221,102,255 · R-G=119).
    // 색만 비슷한 파랑·라벤더 계열(맵 썸네일 아이콘 파편 R-G≤45, 기둥 장식 R-G≤48, R≤156)과
    // 여기서 갈린다(08-04 퀸스로드: 룬 없는데 썸네일 파편 7px가 통과해 헛걸음한 사례).
    private const int RuneMinMeanR = 180;
    private const int RuneMinMeanRG = 75;

    /// <summary>미니맵 영역에서 룬(보라 다이아) 아이콘 중심을 찾는다(minimapRect 상대, 서브픽셀).
    /// 후보가 여럿이면 가장 큰 블롭. 없으면 null. (룬 사용 시작 시 1회만 측정하는 용도)</summary>
    public static PointF? FindRuneIcon(Bitmap frame, Rectangle minimapRect)
    {
        PointF? best = null; double bestScore = double.MinValue;
        foreach (var b in ScanRuneBlobs(frame, minimapRect, out _, out _, out _))
            if (b.Pass && b.Count > bestScore) { bestScore = b.Count; best = b.Center; }
        return best;
    }

    /// <summary>룬 탐지 후보 블롭 하나 — 게이트 판정 결과(Pass/탈락 사유)와 평균 색 포함(진단용).</summary>
    internal readonly record struct RuneBlob(PointF Center, int Count, int W, int H, double Fill,
                                             int MeanR, int MeanG, int MeanB, bool Pass, string Note);

    /// <summary>보라 마스크 → 블롭 분해 → 게이트 판정까지 프로덕션 경로 그대로 수행하고 후보 전부를
    /// 돌려준다. FindRuneIcon(실전)과 --rune-minimap-analyze(진단)가 같은 코드를 쓰기 위한 공용부.</summary>
    internal static List<RuneBlob> ScanRuneBlobs(Bitmap frame, Rectangle minimapRect, out bool[] mask, out int mw, out int mh)
    {
        var blobs = new List<RuneBlob>();
        var area = Rectangle.Intersect(minimapRect, new Rectangle(0, 0, frame.Width, frame.Height));
        mw = Math.Max(0, area.Width); mh = Math.Max(0, area.Height);
        mask = new bool[mw * mh];
        if (mw <= 0 || mh <= 0) return blobs;

        int w = mw, h = mh;
        var rs = new byte[w * h]; var gs = new byte[w * h]; var bs = new byte[w * h];
        var data = frame.LockBits(area, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            unsafe
            {
                for (int y = 0; y < h; y++)
                {
                    byte* row = (byte*)data.Scan0 + y * data.Stride;
                    int o = y * w;
                    for (int x = 0; x < w; x++)
                    {
                        byte b = row[x * 4], g = row[x * 4 + 1], r = row[x * 4 + 2];
                        rs[o + x] = r; gs[o + x] = g; bs[o + x] = b;
                        // 보라·라벤더: 파랑 우세 + 빨강 동반 + 초록 낮음
                        mask[o + x] = b >= 170 && r >= 100 && b - g >= 60;
                    }
                }
            }
        }
        finally { frame.UnlockBits(data); }

        var seen = new bool[w * h];
        var stack = new Stack<int>();
        float ox = area.X - minimapRect.X, oy = area.Y - minimapRect.Y;
        for (int i = 0; i < mask.Length; i++)
        {
            if (!mask[i] || seen[i]) continue;
            long sumX = 0, sumY = 0, sumR = 0, sumG = 0, sumB = 0; int count = 0;
            int minX = w, maxX = -1, minY = h, maxY = -1;
            stack.Push(i); seen[i] = true;
            while (stack.Count > 0)
            {
                int p = stack.Pop();
                int px = p % w, py = p / w;
                sumX += px; sumY += py; count++;
                sumR += rs[p]; sumG += gs[p]; sumB += bs[p];
                if (px < minX) minX = px; if (px > maxX) maxX = px;
                if (py < minY) minY = py; if (py > maxY) maxY = py;
                if (px > 0 && mask[p - 1] && !seen[p - 1]) { seen[p - 1] = true; stack.Push(p - 1); }
                if (px < w - 1 && mask[p + 1] && !seen[p + 1]) { seen[p + 1] = true; stack.Push(p + 1); }
                if (py > 0 && mask[p - w] && !seen[p - w]) { seen[p - w] = true; stack.Push(p - w); }
                if (py < h - 1 && mask[p + w] && !seen[p + w]) { seen[p + w] = true; stack.Push(p + w); }
            }
            int bw = maxX - minX + 1, bh = maxY - minY + 1;
            double fill = count / (double)(bw * bh);
            var c = new PointF((float)sumX / count + ox, (float)sumY / count + oy);
            // 게이트 체인 — 첫 탈락 사유를 기록(진단 출력용). 실전 판정은 Pass만 사용.
            bool pass; string note = "";
            if (count < RuneMinBlobArea || count > RuneMaxBlobArea) { pass = false; note = "면적"; }
            else if (bw > RuneMaxBlobBox || bh > RuneMaxBlobBox) { pass = false; note = "박스"; }
            // 모양 게이트 — 룬은 다이아(◆)라 바운딩박스의 절반만 채운다(≈0.5). 꽉 찬 원(≈0.79)·
            // 사각(≈1.0)인 보라 계열 NPC 마커가 색만으로 통과해 NPC에게 말을 걸었다(08-04 보고).
            // 침식된 작은 코어(면적<15)는 모양 판정이 무의미해 통과시킨다.
            else if (count >= 15 && fill > 0.68) { pass = false; note = "채움비"; }
            else if (Math.Max(bw, bh) > Math.Min(bw, bh) * 2.2) { pass = false; note = "길쭉"; } // 장식·라벨 배제
            else if (sumR / count < RuneMinMeanR || (sumR - sumG) / count < RuneMinMeanRG) { pass = false; note = "색"; } // 마젠타 아님
            else pass = true;
            blobs.Add(new RuneBlob(c, count, bw, bh, fill,
                (int)(sumR / count), (int)(sumG / count), (int)(sumB / count), pass, note));
        }
        return blobs;
    }

    /// <summary>--rune-minimap-analyze 진입점 — 저장된 rune-minimap.png로 룬 아이콘 탐지를 오프라인 재현.
    /// 이미지 전체를 미니맵 영역으로 보고 후보 블롭 전부(게이트 판정 사유·평균 색 포함)와 최종 선택을
    /// &lt;파일&gt;.rune.txt 로, 보라 마스크를 &lt;파일&gt;.rune-mask.png 로 남긴다.</summary>
    public static void AnalyzeRuneToFile(string[] paths)
    {
        foreach (var path in paths)
        {
            try
            {
                using var bmp = new Bitmap(path);
                var rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
                var blobs = ScanRuneBlobs(bmp, rect, out var mask, out int mw, out int mh);
                var sb = new System.Text.StringBuilder();
                sb.AppendLine($"{Path.GetFileName(path)} {bmp.Width}x{bmp.Height} — 보라 마스크 {mask.Count(x => x)}px, 블롭 {blobs.Count}개");
                foreach (var b in blobs.OrderByDescending(x => x.Count))
                    sb.AppendLine($"  ({b.Center.X,6:F1},{b.Center.Y,6:F1}) a{b.Count,-4} {b.W}x{b.H} 채움{b.Fill:F2} RGB({b.MeanR},{b.MeanG},{b.MeanB}) {(b.Pass ? "★통과" : "탈락:" + b.Note)}");
                var pick = FindRuneIcon(bmp, rect);
                sb.AppendLine(pick is { } p ? $"→ 룬 판정: ({p.X:F1},{p.Y:F1})" : "→ 룬 없음");
                File.WriteAllText(path + ".rune.txt", sb.ToString());
                using var mb = new Bitmap(mw, mh);
                for (int y = 0; y < mh; y++)
                    for (int x = 0; x < mw; x++)
                        mb.SetPixel(x, y, mask[y * mw + x] ? Color.White : Color.Black);
                mb.Save(path + ".rune-mask.png");
            }
            catch (Exception ex) { try { File.WriteAllText(path + ".rune.txt", "분석 실패: " + ex.Message); } catch { /* 무시 */ } }
        }
    }

    // ---------- 검은 창(미니맵 패널) 기반 탐지 ----------
    // 실제 미니맵 창 구조(스크린샷 확인): 어두운 제목줄 + 어두운 테두리 안에 흰 테두리의 '컬러 맵'이
    // 들어있다 — 즉 속이 꽉 찬 검은 상자가 아니라 '어두운 프레임(액자)' 모양이다. 그래서 채움 비율
    // 대신 바운딩박스 '테두리 커버리지'(둘레가 어두운 비율)로 사각 창을 판정한다.
    private const int PanelScale = 4;      // 1/4 해상도로 어두운 영역 스캔(성능)
    private const int PanelMinW = 90;      // 미니맵 창 최소 크기(full px)
    private const int PanelMinH = 80;
    private const double PanelMaxFrac = 0.6;   // 창의 60% 넘는 어두운 덩어리는 패널이 아니라 어두운 맵 배경
    private const double PanelMinFill = 0.12;  // 프레임 모양이라 내부는 비어도 됨 — 최소한만
    private const double PanelMinBorder = 0.55; // 바운딩박스 둘레의 어두운 비율(모서리 라운드 감안)

    /// <summary>화면에서 어두운 프레임(미니맵 창 챠시) 후보를 찾는다.</summary>
    public static List<Rectangle> FindDarkPanels(Bitmap frame, int maxLum = 70)
    {
        var list = new List<Rectangle>();
        int w = frame.Width / PanelScale, h = frame.Height / PanelScale;
        if (w < 8 || h < 8) return list;

        var mask = new bool[w * h];
        var data = frame.LockBits(new Rectangle(0, 0, frame.Width, frame.Height),
                                  ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            unsafe
            {
                for (int y = 0; y < h; y++)
                {
                    byte* row = (byte*)data.Scan0 + y * PanelScale * data.Stride;
                    int o = y * w;
                    for (int x = 0; x < w; x++)
                    {
                        int px = x * PanelScale * 4;
                        byte b = row[px], g = row[px + 1], r = row[px + 2];
                        mask[o + x] = (r * 299 + g * 587 + b * 114) / 1000 <= maxLum;
                    }
                }
            }
        }
        finally { frame.UnlockBits(data); }

        var seen = new bool[w * h];
        var stack = new Stack<int>();
        int minW = PanelMinW / PanelScale, minH = PanelMinH / PanelScale;
        int maxW = (int)(w * PanelMaxFrac), maxH = (int)(h * PanelMaxFrac);
        for (int i = 0; i < mask.Length; i++)
        {
            if (!mask[i] || seen[i]) continue;
            int count = 0, minX = w, maxX = -1, minY = h, maxY = -1;
            stack.Push(i); seen[i] = true;
            while (stack.Count > 0)
            {
                int p = stack.Pop();
                int px = p % w, py = p / w;
                count++;
                if (px < minX) minX = px; if (px > maxX) maxX = px;
                if (py < minY) minY = py; if (py > maxY) maxY = py;
                if (px > 0 && mask[p - 1] && !seen[p - 1]) { seen[p - 1] = true; stack.Push(p - 1); }
                if (px < w - 1 && mask[p + 1] && !seen[p + 1]) { seen[p + 1] = true; stack.Push(p + 1); }
                if (py > 0 && mask[p - w] && !seen[p - w]) { seen[p - w] = true; stack.Push(p - w); }
                if (py < h - 1 && mask[p + w] && !seen[p + w]) { seen[p + w] = true; stack.Push(p + w); }
            }
            int bw = maxX - minX + 1, bh = maxY - minY + 1;
            if (bw < minW || bh < minH || bw > maxW || bh > maxH) continue;
            if ((double)count / (bw * bh) < PanelMinFill) continue; // 최소 실체는 있어야 함

            // 프레임(액자) 판정: 바운딩박스 둘레가 충분히 어두운가 — 제목줄+테두리가 둘레를 이룬다.
            int borderCells = 0, borderDark = 0;
            for (int x = minX; x <= maxX; x++)
            {
                borderCells += 2;
                if (mask[minY * w + x]) borderDark++;
                if (mask[maxY * w + x]) borderDark++;
            }
            for (int y = minY + 1; y < maxY; y++)
            {
                borderCells += 2;
                if (mask[y * w + minX]) borderDark++;
                if (mask[y * w + maxX]) borderDark++;
            }
            if (borderCells > 0 && (double)borderDark / borderCells < PanelMinBorder) continue;

            list.Add(new Rectangle(minX * PanelScale, minY * PanelScale, bw * PanelScale, bh * PanelScale));
        }
        return list;
    }

    private const int InnerMinLum = 180; // 맵 영역을 감싸는 흰 테두리 판정 밝기

    /// <summary>검은 챠시 안에서 맵 영역을 감싸는 '흰 테두리 사각형(링)'을 찾는다 — 미니맵 창의
    /// 확실한 지문. 반환 rect = 링 안쪽 맵 영역(프레임 좌표). 못 찾으면 null.</summary>
    public static Rectangle? FindWhiteInnerFrame(Bitmap frame, Rectangle panel)
    {
        var area = Rectangle.Intersect(panel, new Rectangle(0, 0, frame.Width, frame.Height));
        if (area.Width < 40 || area.Height < 40) return null;
        int w = area.Width, h = area.Height;
        var mask = new bool[w * h];
        var data = frame.LockBits(area, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            unsafe
            {
                for (int y = 0; y < h; y++)
                {
                    byte* row = (byte*)data.Scan0 + y * data.Stride;
                    int o = y * w;
                    for (int x = 0; x < w; x++)
                    {
                        byte b = row[x * 4], g = row[x * 4 + 1], r = row[x * 4 + 2];
                        mask[o + x] = (r * 299 + g * 587 + b * 114) / 1000 >= InnerMinLum;
                    }
                }
            }
        }
        finally { frame.UnlockBits(data); }

        var seen = new bool[w * h];
        var stack = new Stack<int>();
        Rectangle best = Rectangle.Empty; long bestArea = 0;
        for (int i = 0; i < mask.Length; i++)
        {
            if (!mask[i] || seen[i]) continue;
            int count = 0, minX = w, maxX = -1, minY = h, maxY = -1;
            stack.Push(i); seen[i] = true;
            while (stack.Count > 0)
            {
                int p = stack.Pop();
                int px = p % w, py = p / w;
                count++;
                if (px < minX) minX = px; if (px > maxX) maxX = px;
                if (py < minY) minY = py; if (py > maxY) maxY = py;
                if (px > 0 && mask[p - 1] && !seen[p - 1]) { seen[p - 1] = true; stack.Push(p - 1); }
                if (px < w - 1 && mask[p + 1] && !seen[p + 1]) { seen[p + 1] = true; stack.Push(p + 1); }
                if (py > 0 && mask[p - w] && !seen[p - w]) { seen[p - w] = true; stack.Push(p - w); }
                if (py < h - 1 && mask[p + w] && !seen[p + w]) { seen[p + w] = true; stack.Push(p + w); }
            }
            int bw = maxX - minX + 1, bh = maxY - minY + 1;
            // 링 조건: 패널 폭 대부분을 차지하는 큰 사각 윤곽 + 얇음(꽉 찬 상자 아님) + 둘레가 밝음
            if (bw < w * 0.6 || bh < h * 0.3) continue;
            if ((double)count / (bw * bh) > 0.6) continue; // 꽉 찬 흰 상자 제외(링/링+지형만 허용)
            int borderCells = 0, borderLit = 0;
            for (int x = minX; x <= maxX; x++)
            {
                borderCells += 2;
                if (mask[minY * w + x]) borderLit++;
                if (mask[maxY * w + x]) borderLit++;
            }
            for (int y = minY + 1; y < maxY; y++)
            {
                borderCells += 2;
                if (mask[y * w + minX]) borderLit++;
                if (mask[y * w + maxX]) borderLit++;
            }
            if (borderCells == 0 || (double)borderLit / borderCells < 0.45) continue;
            long a = (long)bw * bh;
            if (a > bestArea) { bestArea = a; best = new Rectangle(area.X + minX + 2, area.Y + minY + 2, bw - 4, bh - 4); }
        }
        return best.IsEmpty ? null : best;
    }

    /// <summary>
    /// 미니맵 탐지의 메인 진입점 — ① 검은 챠시 후보를 찾고 ② 그 안의 '흰 테두리(맵 영역)'를 확인,
    /// ③ 노란 점이 들어있는 창을 미니맵으로 확정한다(흰 테두리 있는 창 우선, 그다음 점 후보가 적은 창 —
    /// 노란 글자 많은 채팅창 배제). 흰 테두리를 찾으면 그 안쪽만 점 탐색(제목줄 아이콘 배제).
    /// 챠시를 못 찾으면 화면 전체 점 스캔으로 폴백. dot은 창(프레임) 상대 좌표.
    /// </summary>
    public static bool TryDetect(Bitmap frame, out Rectangle panel, out Rectangle mapArea, out PointF dot, out int candidateCount,
                                 int minR = 200, int minG = 180, int maxB = 120, int panelMaxLum = 70, PointF? near = null)
    {
        panel = Rectangle.Empty; mapArea = Rectangle.Empty; dot = PointF.Empty; candidateCount = 0;

        var found = new List<(Rectangle Chassis, Rectangle Search, bool Ring, List<DotCandidate> Dots)>();
        foreach (var p in FindDarkPanels(frame, panelMaxLum))
        {
            var inner = FindWhiteInnerFrame(frame, p);
            var search = inner ?? p;
            var dots = FindDots(frame, search, minR, minG, maxB);
            if (dots.Count == 0) continue;
            found.Add((p, search, inner is not null, dots));
        }

        if (found.Count > 0)
        {
            var pick = found.OrderByDescending(f => f.Ring).ThenBy(f => f.Dots.Count).First();
            // FindDots 좌표는 탐색 영역 상대 → 프레임 상대로 변환
            var frameDots = pick.Dots.Select(c => c with { Center = new PointF(c.Center.X + pick.Search.X, c.Center.Y + pick.Search.Y) }).ToList();
            panel = pick.Chassis;
            mapArea = pick.Ring ? pick.Search : Rectangle.Empty;
            candidateCount = frameDots.Count;
            dot = Pick(frameDots, near).Center;
            return true;
        }

        // 폴백: 검은 챠시 미탐지(테마·투명도 차이 등) → 화면 전체 점 스캔
        var all = FindDots(frame, new Rectangle(0, 0, frame.Width, frame.Height), minR, minG, maxB);
        if (all.Count == 0) return false;
        candidateCount = all.Count;
        dot = Pick(all, near).Center;
        return true;
    }
}
