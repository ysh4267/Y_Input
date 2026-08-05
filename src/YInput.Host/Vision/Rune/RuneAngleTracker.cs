namespace YInput.Host.Vision;

/// <summary>회전 화살표의 각도 시계열 판정 — 머리 반전 교정·플립 고착 감지·회전 판정·반동 투표.
/// 상태 없는 순수 함수 모음이라 실전 관찰 루프와 오프라인 리플레이가 같은 코드를 공유한다.
/// (2026-08-04 모듈화: PositionWatcher 1307–1429에서 이동 — 본문·근거 주석 원문 유지)</summary>
internal static class RuneAngleTracker
{
    internal const int RecoilHits = 2; // 반동 확정 최소 관측 횟수(같은 방위)

    /// <summary>주축 각도의 180° 머리 판별 반전 교정 — 회전 글리프의 블롭이 조각나면 머리
    /// 다수결이 프레임마다 뒤집혀 각도가 ±180° 튄다. 직전 각도+직전 스텝의 예측치에 더 가까운
    /// 쪽(원값 vs +180°)을 채택한다(2026-08-04 14:13 룬: ↓회전 화살표의 반동 착지 270°가
    /// 90°로 반전 기록 → 가짜 U 2표 오확정). 정지 글리프는 예측=직전값이라 영향 없음.
    /// 주의: 노이즈 표본 하나가 momentum을 뒤집으면 이후 매 표본이 잘못된 쪽을 택하는 교대
    /// 플립에 고착될 수 있다 — 복원은 <see cref="DerailedAngles"/> 리셋이 담당한다(중앙값 스텝
    /// 예측으로 바꿔봤으나 DDRD 세트의 진짜 반동 착지 261°가 306°로 왜곡돼 회귀, 채택 안 함).</summary>
    internal static double FixAngleFlip(List<double> series, double ang)
    {
        int m = series.Count;
        if (m < 2) return ang;
        double Wrap(double d) { d %= 360; if (d > 180) d -= 360; else if (d <= -180) d += 360; return d; }
        double pred = series[m - 1] + Wrap(series[m - 1] - series[m - 2]);
        return Math.Abs(Wrap(ang - pred)) > Math.Abs(Wrap(ang + 180 - pred)) ? (ang + 180) % 360 : ang;
    }

    /// <summary>머리 플립 고착 감지 — 최근 6스텝의 중앙값 속도가 180°/100ms를 넘으면 시계열이
    /// 교대 플립 난수에 빠진 것(20:57 실전: 3번 회전 ←이 노이즈 1표본 후 매 스텝 ~298°/100ms
    /// 동부호 널뜀 → 가짜 표 L·D·R 산개 → 3/4 실패). 진짜 회전 실측 54~90°/100ms의 2배 여유.
    /// 감지 시 호출자가 시계열을 비우면 원시 각도는 매끈하므로 즉시 자가 복원된다(같은 스트립을
    /// 깨끗한 상태에서 리플레이하면 매끈한 -31°/50ms 회전이었음이 증거).</summary>
    internal static bool DerailedAngles(List<double> ts, List<double> deg)
    {
        int n = deg.Count;
        if (n < 7) return false;
        var mags = new List<double>(6);
        for (int i = n - 6; i < n; i++)
        {
            double d = (deg[i] - deg[i - 1]) % 360;
            if (d > 180) d -= 360; else if (d <= -180) d += 360;
            mags.Add(Math.Abs(d) * 100 / Math.Max(40, ts[i] - ts[i - 1]));
        }
        mags.Sort();
        return mags[3] > 180;
    }

    /// <summary>각도 시계열이 '회전 중'인가 — 최근 3스텝이 전부 같은 방향으로 ≥15°/100ms.
    /// 정지 글리프의 각도 지터는 스텝이 작거나 부호가 요동해 걸리지 않는다.</summary>
    internal static bool IsRotating(List<double> ts, List<double> deg)
    {
        if (deg.Count < 4) return false;
        int sgn = 0;
        for (int k = deg.Count - 3; k < deg.Count; k++)
        {
            double d = (deg[k] - deg[k - 1]) % 360;
            if (d > 180) d -= 360; else if (d <= -180) d += 360;
            double rate = d * 100 / Math.Max(40, ts[k] - ts[k - 1]);
            if (Math.Abs(rate) < 15) return false;
            int s = Math.Sign(rate);
            if (sgn == 0) sgn = s; else if (s != sgn) return false;
        }
        return true;
    }

    /// <summary>축 안정 — 최근 3표본의 주축 각도(mod 180, 머리 반전 무시)가 ±25° 안에 모여야 정지 잠금.
    /// 관측점이 화살표 사이·잡영역에 있으면 시그니처가 우연히 3연속 비슷해도 각도는 난수다
    /// (16:11 실행: 60px 잡줄로 슬롯이 한 칸 어긋나 옆 ↑화살표 걸침 읽기 → 4번 U 오답 락 —
    ///  당시 각도 시계열 84→260→1→108→243→30… 난수인데도 잠겼다).</summary>
    internal static bool AxisStable(List<double> v)
    {
        if (v.Count < 3) return false;
        static double Axis(double d) { d %= 180; if (d < 0) d += 180; return d; }
        double a0 = Axis(v[^3]);
        for (int k = 2; k >= 1; k--)
        {
            double diff = Math.Abs(Axis(v[^k]) - a0);
            if (Math.Min(diff, 180 - diff) > 25) return false;
        }
        return true;
    }

    // '방향-주축 일치'(DirAxisConsistent) 게이트는 2026-08-05 시도 후 기각 — 침식된 진짜 ↑/↓
    // 글리프가 가로 파편만 남아 주축 2°/359°로 재면서도 방향 분류는 정확했다(10:32 스트립 실측:
    // 게이트가 정답 잠금 2개를 차단해 2/4 실패). 근거였던 "진짜 잠금 축 이탈 ≤2°"는 온전한
    // 글리프에서만 성립한다. 잡음 잠금 방어는 위치 단계(웜 교차 검증·슬롯 위치 대응 게이트)가 담당.

    /// <summary>회전 화살표의 반동 감지 — 각도 시계열에서 시간당 회전량(중앙값 각속도) 대비
    /// '스텝 급감(딸깍) 또는 역행'이 생긴 지점의 각도를 4방위로 투표, <see cref="RecoilHits"/>회
    /// 이상 같은 방위에 쌓이고 2위의 2배 이상이면 확정. 각도 0=→, 90=↑ (반시계 양수).</summary>
    internal static void TryDetectRecoil(int j, List<double> ts, List<double> deg, int[,] votes, ref int lockedCount, char?[] locked, Action<string>? diag = null)
    {
        int n = deg.Count;
        if (n < 6) return; // 각속도 기준선을 잡을 최소 표본
        // 스텝(직전→현재), (-180,180]로 래핑, 시간 정규화(°/100ms)
        double Step(int i)
        {
            double d = (deg[i] - deg[i - 1]) % 360;
            if (d > 180) d -= 360; else if (d <= -180) d += 360;
            double dt = Math.Max(40, ts[i] - ts[i - 1]);
            return d * 100 / dt;
        }
        var steps = new List<double>(n - 1);
        for (int i = 1; i < n; i++) steps.Add(Step(i));
        // 최근 표본의 중앙값 크기·부호 = 기준 회전 속도(반동·노이즈에 둔감)
        var absSorted = steps.Select(Math.Abs).OrderBy(x => x).ToList();
        double omega = absSorted[absSorted.Count / 2];
        if (omega < 8) return; // 사실상 정지(정지 화살표는 시그니처 락 경로가 담당)
        // 속도 상식 상한 — 진짜 회전 실측 54~90°/100ms(27~45°/50ms 틱). 머리 플립 고착·앨리어싱
        // 난수는 매 스텝 ~150°/50ms(≈300°/100ms)의 '일관된 초고속 회전'으로 위장한다(20:57 실전:
        // 3번 시계열이 +298°/100ms 동부호로 널뜀 → L·D·R 가짜 표). 물리적으로 불가능한 속도면 무표.
        if (omega > 150) return;
        int rotSign = Math.Sign(steps.Sum());
        if (rotSign == 0) return;
        // 진짜 회전은 스텝 부호가 거의 한 방향 — 정지 화살표의 각도 지터(부호 요동)로
        // 가짜 반동 투표가 쌓이는 것을 막는다
        if (steps.Count(s => Math.Sign(s) == rotSign) < steps.Count * 0.7) return;
        // 기준선 정합 게이트 — 반동은 '일관된 회전' 위의 이상이라야 표가 된다. 마지막(이상 후보)을
        // 뺀 스텝들 중 기준 속도의 0.4~2.2배·동부호가 60% 미만이면 시계열 자체가 오염된 것(플립
        // 잔재·심한 지터)이라 투표하지 않는다 — 가짜 2표 오답 확정을 이중 차단. 진짜 반동(급감·
        // 역행)은 드물어(2바퀴에 1~2회) 정상 시계열의 비율을 60% 아래로 못 끌어내린다.
        var baseSteps = steps.Take(steps.Count - 1).ToList();
        if (baseSteps.Count(s => Math.Sign(s) == rotSign && Math.Abs(s) >= omega * 0.4 && Math.Abs(s) <= omega * 2.2)
            < baseSteps.Count * 0.6) return;

        // 마지막 스텝이 이상(급감 또는 역행)이면 그 구간의 각도를 방위로 투표
        double last = steps[^1];
        bool anomaly = Math.Abs(last) <= omega * 0.35 || Math.Sign(last) != rotSign;
        if (!anomaly) return;
        // 피벗(정답 방위) 추정 = 스냅백 '착지각'(deg[^1]) — 격발은 표본 사이에서 일어나 피벗
        // 자체는 못 찍지만, 스냅백은 항상 피벗 근처(실측 −5~+12°)에 착지한다. fail4 재현: 진짜
        // 반동 4건의 착지각 102/90/355/8° vs 정답 90/90/0/0°. 반면 '격발 직전 표본'(deg[^2])의
        // 오차는 회전속도×샘플 간격에 비례(50ms 틱·33°/틱이면 최대 ~33°, 틱을 놓치면 66°+)라
        // 45° 스냅 경계를 넘어 인접 방위로 투표가 갈라질 수 있다(11:47 실행: 회전 2개 반동
        // 미관측 2/4 실패의 유력 원인 · fail4 재현에서도 2:1 턱걸이 락).
        double at = deg[^1];
        double norm = (at % 360 + 360) % 360;
        int cardinal = (int)Math.Round(norm / 90.0) % 4; // 0=R 1=U 2=L 3=D
        // 착지각 정방위 게이트 — 진짜 스냅백은 정답 방위 −5~+12°에 착지한다(10:14 실측).
        // 캡처 지터로 생긴 가짜 이상(착지 154°·219° 등 방위에서 26~39° 이탈)이 진짜 표만큼
        // 쌓여 오답 확정을 만들었다(2026-08-04 14:04 룬 리플레이: ↓화살표가 L 2표로 오확정).
        double offCardinal = Math.Abs(norm - cardinal * 90);
        if (offCardinal > 180) offCardinal = 360 - offCardinal;
        if (offCardinal > 25)
        {
            diag?.Invoke($"반동표[{j + 1}] {deg[^2]:F0}°→{deg[^1]:F0}° 기각(방위 이탈 {offCardinal:F0}°)");
            return;
        }
        votes[j, cardinal]++;
        diag?.Invoke($"반동표[{j + 1}] {deg[^2]:F0}°→{deg[^1]:F0}° → {cardinal switch { 0 => 'R', 1 => 'U', 2 => 'L', _ => 'D' }}");

        int best = 0, bestC = -1, second = 0;
        for (int c = 0; c < 4; c++)
        {
            if (votes[j, c] > best) { second = best; best = votes[j, c]; bestC = c; }
            else if (votes[j, c] > second) second = votes[j, c];
        }
        if (best >= RecoilHits && best >= second * 2 && locked[j] is null)
        {
            locked[j] = bestC switch { 0 => 'R', 1 => 'U', 2 => 'L', _ => 'D' };
            lockedCount++;
        }
    }
}
