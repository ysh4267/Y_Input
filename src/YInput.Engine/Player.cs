using YInput.Core.Models;
using YInput.Input;

namespace YInput.Engine;

/// <summary>재생 진행 상황(루프 인덱스, 현재 스텝/전체).</summary>
public readonly record struct PlaybackProgress(int Loop, int StepIndex, int StepCount);

/// <summary>
/// 매크로를 <see cref="IInputSink"/>로 순차 재생한다. 속도 배율·반복·취소를 지원하며
/// 스텝 간 지연은 <see cref="PreciseDelay"/>로 정밀하게 대기한다.
/// </summary>
public sealed class Player
{
    private readonly IInputSink _sink;
    private CancellationTokenSource? _cts;

    // 재생 중 '누름(Down)'만 하고 아직 '뗌(Up)'하지 않은 입력 추적 — 정지/종료 시 모두 떼서 스턱(눌린 채 멈춤)을 방지.
    private readonly Dictionary<(ushort code, ushort ext), ushort> _heldKeys = new(); // 값 = Down 상태(E0/E1 확장 플래그 포함)
    private readonly HashSet<ushort> _heldMouseButtons = new();                       // 눌린 마우스 버튼의 Down 비트
    private readonly Dictionary<GamepadControl, int> _heldPad = new();                // 0이 아닌(눌림/기울임) 패드 컨트롤
    private static readonly ushort[] MouseButtonDownBits = { 0x001, 0x004, 0x010, 0x040, 0x100 }; // 좌·우·중·확장4·5 Down 비트(Up=Down<<1)

    public bool IsPlaying { get; private set; }

    public event EventHandler? Started;
    public event EventHandler? Stopped;
    public event EventHandler<PlaybackProgress>? Progress;
    public event EventHandler<Exception>? Failed;

    public Player(IInputSink sink) => _sink = sink;

    /// <summary>지연(ms)에 속도 배율을 적용한 실제 대기 시간. (순수 함수 — 테스트 대상)</summary>
    public static double EffectiveDelayMs(double delayBeforeMs, double speedMultiplier)
    {
        var speed = speedMultiplier <= 0 ? 1.0 : speedMultiplier;
        return delayBeforeMs <= 0 ? 0 : delayBeforeMs / speed;
    }

    /// <summary>
    /// 지연에 ±<paramref name="percent"/>% 무작위 지터를 적용(휴머나이즈). (순수 함수 — 테스트 대상)
    /// 결과는 [ms·(1-p), ms·(1+p)] 범위, 음수는 0으로 클램프.
    /// </summary>
    public static double ApplyJitter(double ms, int percent, Random rng)
    {
        if (percent <= 0 || ms <= 0) return ms;
        double frac = percent / 100.0;
        double factor = 1.0 + (rng.NextDouble() * 2.0 - 1.0) * frac; // [1-frac, 1+frac]
        double result = ms * factor;
        return result < 0 ? 0 : result;
    }

    /// <summary>매크로를 재생한다. 이미 재생 중이면 무시. 백그라운드에서 완료될 때까지 대기 가능.</summary>
    public async Task PlayAsync(Macro macro, CancellationToken external = default)
    {
        if (IsPlaying) return;

        _cts = CancellationTokenSource.CreateLinkedTokenSource(external);
        var ct = _cts.Token;
        IsPlaying = true;
        _heldKeys.Clear(); _heldMouseButtons.Clear(); _heldPad.Clear();
        Started?.Invoke(this, EventArgs.Empty);

        bool interrupted = false; // 정상 종료가 아니라 정지/오류로 끝났는지 — 그때만 눌린 입력을 뗀다
        using var _ = MultimediaTimerScope.HighResolution();
        try
        {
            double speed = macro.SpeedMultiplier;
            int loops = macro.IsInfinite ? int.MaxValue : Math.Max(1, macro.LoopCount);

            var steps = macro.Steps;
            for (int loop = 0; loop < loops && !ct.IsCancellationRequested; loop++)
            {
                // 반복(Loop) 블록을 스택으로 해석: LoopStart/End를 짝지어 본문을 Count회 반복(중첩 가능).
                var loopStack = new Stack<(int bodyStart, int remaining)>();
                int ip = 0;
                while (ip < steps.Count && !ct.IsCancellationRequested)
                {
                    ct.ThrowIfCancellationRequested();
                    var step = steps[ip];

                    switch (step.Event)
                    {
                        case LoopStartEvent ls:
                            loopStack.Push((ip + 1, Math.Max(1, ls.Count)));
                            ip++;
                            break;
                        case LoopEndEvent:
                            if (loopStack.Count > 0)
                            {
                                var top = loopStack.Pop();
                                if (top.remaining > 1) { loopStack.Push((top.bodyStart, top.remaining - 1)); ip = top.bodyStart; }
                                else ip++;
                            }
                            else ip++; // 짝 없는 끝 → 무시
                            break;
                        case DelayEvent de:
                            // 대기는 '지연' 블록에서만 발생(속도·휴머나이즈 적용). 다른 블록은 즉시 실행.
                            // 휴머나이즈는 각 지연 블록이 개별로 가진다(없으면 0 = 흔들림 없음).
                            // 진행 보고는 '대기 시작 시점'에 — 그래야 대기 중 하이라이트가 직전 입력이 아닌 이 지연 행에 간다.
                            Progress?.Invoke(this, new PlaybackProgress(loop, ip, steps.Count));
                            var delay = EffectiveDelayMs(step.DelayBeforeMs, speed);
                            delay = ApplyJitter(delay, de.RandomizePercent, Random.Shared);
                            if (delay > 0)
                                await PreciseDelay.WaitAsync(delay, ct).ConfigureAwait(false);
                            ip++;
                            break;
                        default:
                            _sink.Send(step.Event);
                            TrackHeld(step.Event); // 누름/뗌 상태 갱신(정지 시 떼기 위해)
                            Progress?.Invoke(this, new PlaybackProgress(loop, ip, steps.Count));
                            // 지연이 0인 스텝도 최소 1ms(0.001s) 양보 — 전부 동기로 돌아 스레드를 막거나
                            // 드라이버에 과속 송출하는 것을 방지하고, 스텝마다 취소(정지)가 즉시 먹게 한다.
                            await PreciseDelay.WaitAsync(1, ct).ConfigureAwait(false);
                            ip++;
                            break;
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            interrupted = true; // 사용자 중단(정지)
        }
        catch (Exception ex)
        {
            interrupted = true; // 오류로 중단
            Failed?.Invoke(this, ex);
        }
        finally
        {
            if (interrupted) ReleaseHeldInputs(); // 중단(정지/오류) 시에만 눌린 채 남은 키·버튼·패드를 모두 떼기(스턱 방지)
            IsPlaying = false;
            _cts?.Dispose();
            _cts = null;
            Stopped?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>송출한 입력의 누름/뗌을 추적한다. 키=Up 비트(0x01), 마우스=Down/Up 비트쌍, 패드=값 0 여부.
    /// 텍스트는 백엔드가 키마다 누름+뗌을 완결하므로 추적하지 않는다.</summary>
    private void TrackHeld(InputEvent e)
    {
        switch (e)
        {
            case KeyboardEvent k:
            {
                var id = (k.Code, (ushort)(k.State & 0x06)); // E0(0x02)/E1(0x04) — 같은 스캔코드라도 확장키를 구분
                if ((k.State & 0x01) == 0) _heldKeys[id] = k.State; // Down → 보유
                else _heldKeys.Remove(id);                          // Up → 해제
                break;
            }
            case MouseEvent m:
                foreach (var down in MouseButtonDownBits)
                {
                    if ((m.ButtonState & down) != 0) _heldMouseButtons.Add(down);
                    if ((m.ButtonState & (down << 1)) != 0) _heldMouseButtons.Remove(down);
                }
                break;
            case GamepadEvent g:
                if (g.Value == 0) _heldPad.Remove(g.Control);
                else _heldPad[g.Control] = g.Value;
                break;
        }
    }

    /// <summary>아직 눌린(Down) 채로 남은 키·마우스 버튼·패드 컨트롤을 모두 떼서(Up/중립) 스턱을 방지한다.
    /// 정리 중 송출 실패는 무시한다(드라이버 미준비 등).</summary>
    private void ReleaseHeldInputs()
    {
        foreach (var kv in _heldKeys)
            TrySend(new KeyboardEvent { Code = kv.Key.code, State = (ushort)(kv.Value | 0x01) }); // 같은 키 + Up 비트
        _heldKeys.Clear();

        foreach (var down in _heldMouseButtons)
            TrySend(new MouseEvent { ButtonState = (ushort)(down << 1) }); // 해당 버튼 Up 비트
        _heldMouseButtons.Clear();

        foreach (var kv in _heldPad)
            TrySend(new GamepadEvent { Control = kv.Key, Value = 0 }); // 중립(0)으로
        _heldPad.Clear();
    }

    private void TrySend(InputEvent e)
    {
        try { _sink.Send(e); } catch { /* 정리 중 송출 실패 무시 */ }
    }

    public void Stop() => _cts?.Cancel();
}
