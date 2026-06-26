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
        Started?.Invoke(this, EventArgs.Empty);

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

                    // 지연은 모든 스텝 공통(반복 끝 지연 = 반복 사이 간격으로 동작).
                    var delay = EffectiveDelayMs(step.DelayBeforeMs, speed);
                    delay = ApplyJitter(delay, macro.RandomizeDelayPercent, Random.Shared);
                    if (delay > 0)
                        await PreciseDelay.WaitAsync(delay, ct).ConfigureAwait(false);

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
                        default:
                            _sink.Send(step.Event);
                            Progress?.Invoke(this, new PlaybackProgress(loop, ip, steps.Count));
                            ip++;
                            break;
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 정상 정지
        }
        catch (Exception ex)
        {
            Failed?.Invoke(this, ex);
        }
        finally
        {
            IsPlaying = false;
            _cts?.Dispose();
            _cts = null;
            Stopped?.Invoke(this, EventArgs.Empty);
        }
    }

    public void Stop() => _cts?.Cancel();
}
