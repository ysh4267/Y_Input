using System.Diagnostics;
using System.Runtime.InteropServices;

namespace YInput.Engine;

/// <summary>
/// 멀티미디어 타이머 해상도를 1ms로 올려 <see cref="Task.Delay(int)"/> 지터를 줄인다.
/// using 스코프로 감싸 재생 동안만 적용한다.
/// </summary>
public readonly struct MultimediaTimerScope : IDisposable
{
    [DllImport("winmm.dll")] private static extern uint timeBeginPeriod(uint period);
    [DllImport("winmm.dll")] private static extern uint timeEndPeriod(uint period);

    private readonly uint _period;
    private readonly bool _active;

    private MultimediaTimerScope(uint period)
    {
        _period = period;
        _active = timeBeginPeriod(period) == 0; // TIMERR_NOERROR
    }

    public static MultimediaTimerScope HighResolution() => new(1);

    public void Dispose()
    {
        if (_active) timeEndPeriod(_period);
    }
}

/// <summary>Stopwatch 기반 정밀 대기. 큰 부분은 Task.Delay, 남은 1~2ms는 스핀.</summary>
public static class PreciseDelay
{
    public static async Task WaitAsync(double milliseconds, CancellationToken ct)
    {
        if (milliseconds <= 0) return;

        var sw = Stopwatch.StartNew();
        int coarse = (int)(milliseconds - 1.5);
        if (coarse > 0)
            await Task.Delay(coarse, ct).ConfigureAwait(false);

        while (sw.Elapsed.TotalMilliseconds < milliseconds)
        {
            ct.ThrowIfCancellationRequested();
            Thread.SpinWait(40);
        }
    }
}
