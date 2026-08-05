using System.Drawing;

namespace YInput.Host.Vision;

/// <summary>소스 간 후보 융합(partial) — 단일 마스크가 화살표 4개를 다 못 봐도, 여러 마스크
/// 단계(교집합·차분·채도·애니 차분)가 각자 본 후보를 슬롯별로 합쳐 줄 위치를 복원한다.
/// 발동 조건은 호출자(StepFrame) 소관 — 기존 전 단계(④·⑦)가 실패한 최후 폴백 전용.
/// 반환은 '위치만'(PointF[4]) — 방향·잠금은 로컬 관찰(StepLocal)이 해결하므로, 잘못된 융합
/// 줄은 잠금 실패→"잠금 미달" 안전 종료로 귀결된다(fail-closed 유지, 2026-08-05 설계).</summary>
internal static partial class RuneArrowDetector
{
    /// <summary>후보 출처 태그. 독립 증거 클래스: 교집합(Inter*)=시간 안정성, 차분(Diff*)=발동 전과
    /// 다름, 애니(Anim)=하이라이트 스윕. VividOnly는 클래스 0 — 모든 소스가 채도(∧vivid)를
    /// 공유하므로 채도는 독립 증거가 아니다(ⓑ⊂ⓒ 포함관계: ⓒ 동시출현을 확인으로 세면 무의미).
    /// DiffWarm(ⓑw)은 차분 클래스에 속한다(웜 게이트는 분리 수단이지 독립 증거 축이 아님).</summary>
    internal enum FuseSource { Inter80, Inter45, DiffStill, DiffBefore, VividOnly, Anim, DiffWarm }

    /// <summary>한 프레임의 단계별 줄 후보 풀 — 호출자가 프레임마다 새로 만들고 폐기한다
    /// (프레임 간 누적 금지 — 카메라 이동 시 위치가 어긋난 후보가 섞인다).
    /// AnalyzeFrame이 각 마스크 단계의 게이트 통과 후보를 소스 태깅해 채운다.</summary>
    internal sealed class FusionPool
    {
        internal readonly List<(Blob B, FuseSource Src)> Entries = [];
        internal Rectangle Region;          // 절대 좌표 변환용(후보 좌표는 영역 상대)
        internal int RegionW;               // 부분 줄 외삽의 우측 경계 검사용
        internal int FrameW;                // 간격 게이트(GapFrac*)의 창폭 기준
        internal double BannerCx = -1;      // PickRow 중심 정렬 게이트 기준(영역 상대)
        internal bool Geometrized;

        internal void SetGeometry(Rectangle region, int frameW, double bannerCx)
        {
            Region = region; RegionW = region.Width; FrameW = frameW; BannerCx = bannerCx;
            Geometrized = true;
        }

        internal void Add(FuseSource src, List<Blob> cands)
        {
            foreach (var b in cands) Entries.Add((b, src));
        }

        internal string Stats()
        {
            int C(FuseSource s) => Entries.Count(e => e.Src == s);
            return $"융합 풀 교80 {C(FuseSource.Inter80)}·교45 {C(FuseSource.Inter45)}·정지 {C(FuseSource.DiffStill)}"
                 + $"·차분 {C(FuseSource.DiffBefore)}·웜차분 {C(FuseSource.DiffWarm)}·채도 {C(FuseSource.VividOnly)}·애니 {C(FuseSource.Anim)}";
        }
    }

    // ---- 융합 게이트 상수 ----
    // 2026-08-05 캘리브레이션(13개 픽스처 + 실패 보존 6폴더 전수 리플레이) 요지:
    //  · ④실패+⑦실패(실전 발동 조건) 케이스는 기존 코퍼스에 없음 — 게이트는 보수 동작 확인이 기준
    //  · 클래스 게이트가 ⓪단독 풀(교80+교45 소스 2개, 증거 1클래스)을 전부 기각 — 오발동 0건
    //  · ⑦이 정답이고 ⑧이 잡줄인 케이스 존재(DLUU) — ⑦ 우선 폴백 순서의 실측 근거
    /// <summary>군집 반경 — 같은 물리 화살표를 두 마스크가 다르게 잡은 블롭의 중심 산포 흡수.
    /// 하한: MergePx=12 초과(고채도 침식 블롭의 중심 드리프트 — purple4 sat80 a263 vs 완화 a439).
    /// 상한: MinSlotSepPx=24 미만이고 실측 최소 이웃 간격 49px(카르시온)의 절반(24.5) 미만이어야
    /// 이웃 화살표끼리 병합 불가. 코퍼스 리플레이에서 이웃 병합·과분리 징후 없음.</summary>
    private const int FuseClusterPx = 16;

    /// <summary>교차 확인 가산 — srcBonus = Area × 이 값 × clamp(클래스 수−1, 0, 2).
    /// 2클래스(교차 확인) 블롭의 유효 점수 ×1.5, 3클래스 ×2.0 — 잡줄의 면적 우세(실측 최대
    /// 1.8배, 20:38)를 다클래스 진짜 줄이 이기고, 무확인(1클래스) 블롭은 가산 없음.</summary>
    private const double FuseSrcBonusFrac = 0.5;

    /// <summary>독립 증거 0개(채도 단독) 군집의 입장 면적 하한 — 잡파편(실측 a50~150)의 하단을
    /// 차단. TryRelocate의 실존 하한 a40보다 보수적.</summary>
    private const int FuseVividOnlyMinArea = 60;

    /// <summary>채택 조합(4개 또는 부분 3개) 중 '증거 클래스 2개 이상' 블롭의 최소 수 — 단일
    /// 클래스에서만 나온 그럴듯한 잡줄(이펙트 병합류) 차단. 소스 수가 아니라 <b>클래스 수</b>다:
    /// 교80+교45는 같은 교집합 증거의 임계 2회라 교차 확인이 아니다(2026-08-05 캘리브레이션:
    /// ⓪만 돈 풀이 소스 수 기준으로 전부 '확인'돼 무의미했다). 진짜 화살표는 마스크가 정상일 때
    /// 정지=INT+DIFF, 회전=DIFF+ANIM으로 자연히 2클래스가 된다.</summary>
    private const int FuseMinCrossConfirmed = 2;
    private const int FuseUnconfirmedAreaCap = 500; // 무확인(클래스<2) 군집 유효 면적 상한 — 안전창 411~677의 중앙(Bonus 주석 참조)

    /// <summary>군집 하나 — 대표 블롭 + 증거 메타. Rep = 멤버 면적 중앙값(짝수면 큰 쪽):
    /// 최대 면적은 병합 비대 블롭, 최소 면적은 침식 조각이라 양극단을 회피한다.</summary>
    private sealed record FusedCand(Blob Rep, int SrcMask, int ClassCount, int SrcCount)
    {
        internal bool Has(FuseSource s) => (SrcMask & (1 << (int)s)) != 0;

        internal string Tags()
        {
            string t = "";
            if (Has(FuseSource.Inter80) || Has(FuseSource.Inter45)) t += "교";
            if (Has(FuseSource.DiffStill) || Has(FuseSource.DiffBefore) || Has(FuseSource.DiffWarm)) t += "차";
            if (Has(FuseSource.Anim)) t += "애";
            if (Has(FuseSource.VividOnly)) t += "채";
            return t;
        }
    }

    /// <summary>소스 태깅 후보들을 군집화 — 면적 내림차순으로 앵커를 고정하고 반경 내만 흡수.
    /// 체인 병합(MergeNear식) 금지: 반경 16으로 체인을 허용하면 잡블롭이 다리가 되어 이웃
    /// 화살표 두 개가 한 군집으로 붕괴할 수 있다.</summary>
    private static List<FusedCand> ClusterCands(List<(Blob B, FuseSource Src)> entries)
    {
        var anchors = new List<(Blob Anchor, List<(Blob B, FuseSource Src)> Members)>();
        foreach (var e in entries.OrderByDescending(x => x.B.Area))
        {
            var slot = anchors.FirstOrDefault(a =>
            {
                double dx = a.Anchor.Cx - e.B.Cx, dy = a.Anchor.Cy - e.B.Cy;
                return dx * dx + dy * dy <= FuseClusterPx * (double)FuseClusterPx;
            });
            if (slot.Members is not null) slot.Members.Add(e);
            else anchors.Add((e.B, [e]));
        }
        var result = new List<FusedCand>(anchors.Count);
        foreach (var (_, members) in anchors)
        {
            var byArea = members.OrderBy(m => m.B.Area).ToList();
            var rep = byArea[byArea.Count / 2].B; // 면적 중앙값(짝수면 큰 쪽)
            int srcMask = 0;
            foreach (var m in members) srcMask |= 1 << (int)m.Src;
            int classCount = 0;
            if ((srcMask & ((1 << (int)FuseSource.Inter80) | (1 << (int)FuseSource.Inter45))) != 0) classCount++;
            if ((srcMask & ((1 << (int)FuseSource.DiffStill) | (1 << (int)FuseSource.DiffBefore)
                            | (1 << (int)FuseSource.DiffWarm))) != 0) classCount++;
            if ((srcMask & (1 << (int)FuseSource.Anim)) != 0) classCount++;
            int srcCount = System.Numerics.BitOperations.PopCount((uint)srcMask);
            result.Add(new FusedCand(rep, srcMask, classCount, srcCount));
        }
        return result;
    }

    /// <summary>소스 간 후보 융합 폴백 — 군집화 → PickRow(교차 확인 가산) 4개 → 실패 시
    /// 부분 줄(3개+외삽, 전 외삽 슬롯 탐침 검증). 위치만 반환(방향·잠금은 로컬 관찰).
    /// contribution = 항상 채워지는 진단 요약(성공 시 슬롯별 기여, 실패 시 사유).</summary>
    internal static PointF[]? TryFusedRow(FusionPool pool, Bitmap frame, Bitmap? beforeRef, out string contribution)
    {
        if (!pool.Geometrized || pool.Entries.Count == 0) { contribution = "융합 풀 비어 있음"; return null; }
        var clusters = ClusterCands(pool.Entries);
        var meta = new Dictionary<Blob, FusedCand>();
        var reps = new List<Blob>();
        foreach (var c in clusters)
        {
            // 입장 게이트 — 독립 증거 없는(채도 단독) 저면적 파편은 풀에 넣지 않는다
            if (c.ClassCount == 0 && c.Rep.Area < FuseVividOnlyMinArea) continue;
            reps.Add(c.Rep); meta[c.Rep] = c;
        }
        string baseStat = $"군집 {clusters.Count}(게이트 후 {reps.Count})";
        if (reps.Count < 3) { contribution = $"{baseStat} — 후보 부족"; return null; }

        // 무확인 면적 캡(2026-08-05 밴드 확장 상시화 캘리브레이션) — 교차확인 없는(클래스 <2)
        // 군집의 유효 면적을 FuseUnconfirmedAreaCap으로 클램프(초과분을 음수 보너스로 상쇄).
        // LDUU 실측: 교80 단독 잡 a1929(마진 0.01 무방향)가 면적 지배로 진짜 조합(전원 교차확인,
        // 4위 −361.4)을 눌러 ⑧이 0좌표로 무너졌다. 캡 안전창은 산술 도출 411~677 — 단일클래스
        // 진짜 슬롯 실측 최대 a411(LDUU 654 웜 단독)은 무손상, 잡 a832·a1929는 상쇄되어 진짜가
        // 9% 마진으로 복원된다. 하드 제거가 아니라 점수 클램프라 군집·게이트·박스벌점은 원값.
        double Bonus(Blob b) => meta.TryGetValue(b, out var m)
            ? b.Area * FuseSrcBonusFrac * Math.Clamp(m.ClassCount - 1, 0, 2)
              - (m.ClassCount < 2 ? Math.Max(0, b.Area - FuseUnconfirmedAreaCap) : 0)
            : 0;
        var region = pool.Region;

        // ① 4개 융합 줄 — 기존 PickRow 게이트(y밴드·간격·중심·면적비) 전부 동일 적용
        var row = PickRow(reps, pool.BannerCx, pool.FrameW, Bonus);
        if (row is not null)
        {
            int confirmed = row.Count(b => meta.TryGetValue(b, out var m) && m.ClassCount >= 2);
            if (confirmed >= FuseMinCrossConfirmed)
            {
                contribution = $"{baseStat} — " + string.Join(" ", row.Select((b, i) =>
                    $"{i + 1}:{(meta.TryGetValue(b, out var m) ? m.Tags() : "?")}"));
                return row.Select(b => new PointF((float)(region.X + b.Cx), (float)(region.Y + b.Cy))).ToArray();
            }
            contribution = $"{baseStat} — 4조합 교차확인 {confirmed}/{FuseMinCrossConfirmed} 미달";
        }
        else contribution = $"{baseStat} — 4조합 없음";

        // ② 부분 줄(3개+외삽) — 모든 외삽 슬롯이 탐침(글리프 실존)을 통과해야 하고, 재구성
        // 4슬롯 평균이 필 중심 게이트(±RowCenterTolFrac)를 통과해야 채택(2026-08-05 09:20 오답 재발 방지)
        var pp = PartialRowFromCands(reps, frame, beforeRef, region, pool.RegionW, pool.FrameW,
            pool.BannerCx, verifyExtrap: true, out var chosen);
        if (pp is null || chosen is null) { contribution += " · 부분 실패"; return null; }
        int confirmed3 = chosen.Count(b => meta.TryGetValue(b, out var m) && m.ClassCount >= 2);
        if (confirmed3 < FuseMinCrossConfirmed)
        {
            contribution += $" · 부분 교차확인 {confirmed3}/{FuseMinCrossConfirmed} 미달";
            return null;
        }
        var slotDesc = pp.Select((p, i) =>
        {
            var m = chosen.FirstOrDefault(b =>
                Math.Abs(region.X + b.Cx - p.X) < 0.6 && Math.Abs(region.Y + b.Cy - p.Y) < 22);
            return $"{i + 1}:{(m is null ? "외삽탐침" : meta[m].Tags())}";
        });
        contribution = $"{baseStat} — 부분 {string.Join(" ", slotDesc)}";
        return pp;
    }
}
