using System.Drawing;
using System.Drawing.Imaging;

namespace YInput.Host.Vision;

/// <summary>마스크·블롭 프리미티브 — 채도/차분 마스크, 얇은 구조 제거, 연결성분·병합(partial).</summary>
internal static partial class RuneArrowDetector
{
    private const int DiffMin = 60;      // 발동 전후 차분(폴백) 임계
    private const int AnimDiffMin = 40;  // 시간차(애니메이션) 차분 임계 — 하이라이트는 변화가 은은할 수 있다

    private const int MinThick = 4;      // 얇은 구조 제거: 가로·세로 연속 두께 하한 — 화살표 코어(반투명
                                         // 합성으로 작아짐)는 살리고 1~3px 외곽선·궤적만 지운다
    private const int MinPieceArea = 4;  // 블롭 조각 최소 픽셀 — 반투명 합성으로 화살표가 점묘처럼
                                         // 흩어지므로 작은 조각도 살려 병합 단계에서 모은다
    private const int MergePx = 12;      // 같은 화살표로 병합하는 조각 간 거리(넓으면 외곽선 조각이 붙는다)

    /// <summary>행별 픽셀 차이 개수(|ΔR|+|ΔG|+|ΔB| ≥ min), 행 평균 밝기, 행 밝기 표준편차(a·b 각각).
    /// 표준편차는 배너 판정용 — 어두운 반투명 띠가 덮이면 행의 명암 대비가 눌려 표준편차가 떨어진다.</summary>
    private static (int[] Diff, double[] LumA, double[] LumB, double[] StdA, double[] StdB) RowStats(Bitmap a, Bitmap b, Rectangle region, int min)
    {
        int w = region.Width, h = region.Height;
        var counts = new int[h];
        var lumA = new double[h];
        var lumB = new double[h];
        var stdA = new double[h];
        var stdB = new double[h];
        var da = a.LockBits(region, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var db = b.LockBits(region, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            unsafe
            {
                for (int y = 0; y < h; y++)
                {
                    byte* ra = (byte*)da.Scan0 + y * da.Stride;
                    byte* rb = (byte*)db.Scan0 + y * db.Stride;
                    int c = 0; long sa = 0, sb2 = 0, qa = 0, qb = 0;
                    for (int x = 0; x < w; x++)
                    {
                        int i4 = x * 4;
                        int d = Math.Abs(ra[i4] - rb[i4]) + Math.Abs(ra[i4 + 1] - rb[i4 + 1]) + Math.Abs(ra[i4 + 2] - rb[i4 + 2]);
                        if (d >= min) c++;
                        int la = ra[i4] + ra[i4 + 1] + ra[i4 + 2];
                        int lb2 = rb[i4] + rb[i4 + 1] + rb[i4 + 2];
                        sa += la; sb2 += lb2;
                        qa += (long)la * la; qb += (long)lb2 * lb2;
                    }
                    counts[y] = c;
                    double ma = sa / (double)w, mb = sb2 / (double)w;
                    lumA[y] = ma / 3.0;
                    lumB[y] = mb / 3.0;
                    stdA[y] = Math.Sqrt(Math.Max(0, qa / (double)w - ma * ma)) / 3.0;
                    stdB[y] = Math.Sqrt(Math.Max(0, qb / (double)w - mb * mb)) / 3.0;
                }
            }
        }
        finally { a.UnlockBits(da); b.UnlockBits(db); }
        return (counts, lumA, lumB, stdA, stdB);
    }

    /// <summary>밝고 채도 높은 픽셀 마스크. requireWarm = 웜톤/초록만 허용(파랑·청록 우세 배제).
    /// <b>화살표 색은 완전 랜덤이라(사용자 확인) 화살표 탐색 경로는 전부 requireWarm=false</b> —
    /// 파란 배경 클러터는 '발동 전과 다름'(diffBefore)·밴드·줄 조합 제약이 걸러낸다.
    /// requireWarm=true는 색이 고정인 배너 안내 텍스트 신호(PuzzlePresent)에만 쓴다.</summary>
    private static bool[] VividMask(Bitmap frame, Rectangle region, int w, int h, int satMin = VividSat, bool requireWarm = true, int maxMin = VividMax)
    {
        var mask = new bool[w * h];
        var data = frame.LockBits(region, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
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
                        int max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
                        // 웜톤(노랑·빨강) 외에 보라·자홍(r가 g보다 우세)도 화살표 색 — 정지 상태의
                        // 보라 화살표가 여기서 걸러져 4개 줄 구성이 실패했다(2026-08-04 위아래위위 룬).
                        // 하늘·파랑 배경은 g가 높아 r>=g+30을 통과하지 못한다. 파랑 우세인 머리끝은
                        // 여기선 여전히 잘리지만(초고채도 일괄 통과는 반짝이·UI 파랑까지 흡수해 블롭
                        // 크기 초과 탈락 유발 — 실측 확인) 줄 선택 후 GrowGlyph가 복원한다.
                        mask[o + x] = max >= maxMin && max - min >= satMin
                                      && (!requireWarm || r >= b + 30 || g >= b + 30 || r >= g + 30);
                    }
                }
            }
        }
        finally { frame.UnlockBits(data); }
        return mask;
    }

    /// <summary>두 프레임의 픽셀 차이(|ΔR|+|ΔG|+|ΔB| ≥ min)를 changed에 OR 누적.</summary>
    private static void AccumulateDiff(Bitmap a, Bitmap b, Rectangle region, int w, int h, int min, bool[] changed)
    {
        var da = a.LockBits(region, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var db = b.LockBits(region, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            unsafe
            {
                for (int y = 0; y < h; y++)
                {
                    byte* ra = (byte*)da.Scan0 + y * da.Stride;
                    byte* rb = (byte*)db.Scan0 + y * db.Stride;
                    int o = y * w;
                    for (int x = 0; x < w; x++)
                    {
                        if (changed[o + x]) continue;
                        int i4 = x * 4;
                        int d = Math.Abs(ra[i4] - rb[i4]) + Math.Abs(ra[i4 + 1] - rb[i4 + 1]) + Math.Abs(ra[i4 + 2] - rb[i4 + 2]);
                        if (d >= min) changed[o + x] = true;
                    }
                }
            }
        }
        finally { a.UnlockBits(da); b.UnlockBits(db); }
    }

    /// <summary>얇은 구조 제거 — 가로·세로 연속 길이가 모두 MinThick 이상인 픽셀만 유지.
    /// 바 외곽선·글로우 다리·눈송이 궤적은 한 방향이 얇아 지워지고, 화살표 본체는 보존된다.</summary>
    private static void ThinFilter(bool[] mask, int w, int h)
    {
        var hRun = new short[w * h];
        var vRun = new short[w * h];
        for (int y = 0; y < h; y++)
        {
            int run = 0;
            for (int x = 0; x < w; x++) { int i = y * w + x; run = mask[i] ? run + 1 : 0; hRun[i] = (short)run; }
            run = 0;
            for (int x = w - 1; x >= 0; x--)
            {
                int i = y * w + x;
                if (mask[i]) { run = Math.Max(run, hRun[i]); hRun[i] = (short)run; } else run = 0;
            }
        }
        for (int x = 0; x < w; x++)
        {
            int run = 0;
            for (int y = 0; y < h; y++) { int i = y * w + x; run = mask[i] ? run + 1 : 0; vRun[i] = (short)run; }
            run = 0;
            for (int y = h - 1; y >= 0; y--)
            {
                int i = y * w + x;
                if (mask[i]) { run = Math.Max(run, vRun[i]); vRun[i] = (short)run; } else run = 0;
            }
        }
        for (int i = 0; i < mask.Length; i++)
            mask[i] = mask[i] && hRun[i] >= MinThick && vRun[i] >= MinThick;
    }

    private sealed record Blob(double Cx, double Cy, int Area, int MinX, int MinY, int MaxX, int MaxY, List<int> Pixels)
    {
        public int W => MaxX - MinX + 1;
        public int H => MaxY - MinY + 1;
    }

    private static List<Blob> FindBlobs(bool[] mask, int w, int h)
    {
        var list = new List<Blob>();
        var seen = new bool[w * h];
        var stack = new Stack<int>();
        for (int i = 0; i < mask.Length; i++)
        {
            if (!mask[i] || seen[i]) continue;
            long sumX = 0, sumY = 0;
            int minX = w, maxX = -1, minY = h, maxY = -1;
            var px = new List<int>();
            stack.Push(i); seen[i] = true;
            while (stack.Count > 0)
            {
                int p = stack.Pop();
                int x = p % w, y = p / w;
                sumX += x; sumY += y; px.Add(p);
                if (x < minX) minX = x; if (x > maxX) maxX = x;
                if (y < minY) minY = y; if (y > maxY) maxY = y;
                if (x > 0 && mask[p - 1] && !seen[p - 1]) { seen[p - 1] = true; stack.Push(p - 1); }
                if (x < w - 1 && mask[p + 1] && !seen[p + 1]) { seen[p + 1] = true; stack.Push(p + 1); }
                if (y > 0 && mask[p - w] && !seen[p - w]) { seen[p - w] = true; stack.Push(p - w); }
                if (y < h - 1 && mask[p + w] && !seen[p + w]) { seen[p + w] = true; stack.Push(p + w); }
            }
            if (px.Count >= MinPieceArea)
                list.Add(new Blob((double)sumX / px.Count, (double)sumY / px.Count, px.Count, minX, minY, maxX, maxY, px));
        }
        return list;
    }

    /// <summary>가까운 조각 병합 — 그라데이션·외곽선 때문에 한 화살표가 여러 조각으로 나뉘는 것을 흡수.</summary>
    private static List<Blob> MergeNear(List<Blob> blobs)
    {
        var merged = new List<Blob>();
        var used = new bool[blobs.Count];
        for (int i = 0; i < blobs.Count; i++)
        {
            if (used[i]) continue;
            used[i] = true;
            var group = new List<Blob> { blobs[i] };
            bool grew = true;
            while (grew) // 체인 병합(a-b 가깝고 b-c 가까우면 a·b·c 모두 한 화살표)
            {
                grew = false;
                for (int j = 0; j < blobs.Count; j++)
                {
                    if (used[j]) continue;
                    foreach (var g in group)
                    {
                        double dx = blobs[j].Cx - g.Cx, dy = blobs[j].Cy - g.Cy;
                        if (dx * dx + dy * dy > MergePx * (double)MergePx) continue;
                        used[j] = true; group.Add(blobs[j]); grew = true; break;
                    }
                }
            }
            double cx = 0, cy = 0; int area = 0;
            int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;
            var pixels = new List<int>();
            foreach (var g in group)
            {
                cx += g.Cx * g.Area; cy += g.Cy * g.Area; area += g.Area;
                minX = Math.Min(minX, g.MinX); minY = Math.Min(minY, g.MinY);
                maxX = Math.Max(maxX, g.MaxX); maxY = Math.Max(maxY, g.MaxY);
                pixels.AddRange(g.Pixels);
            }
            merged.Add(new Blob(cx / area, cy / area, area, minX, minY, maxX, maxY, pixels));
        }
        return merged;
    }
}
