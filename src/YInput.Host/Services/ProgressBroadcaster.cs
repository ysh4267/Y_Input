using System.Collections.Concurrent;
using YInput.Engine;

namespace YInput.Host.Services;

/// <summary>재생 진행(progress) 브로드캐스트를 스텝 속도와 분리한다.
/// 매크로별 '최신 진행'만 보관하고 ~60Hz 타이머로 변경분만 전송한다(스텝당 ~1000건 → 초당 ~60건).
/// 정지 시 마지막 프레임을 1회 강제 전송한다(코얼레싱·DropOldest로 최종 프레임이 유실되지 않게).
/// progress 외 메시지(status/log/recordedStep/inputMonitor)는 대상 아님 — 그대로 즉시 전송.</summary>
public sealed class ProgressBroadcaster : IDisposable
{
    private const int FlushIntervalMs = 16; // ~60Hz

    private readonly SocketHub _hub;
    private readonly System.Threading.Timer _timer; // WinForms.Timer와 모호 → 완전 한정

    // macroId → 최신 진행.
    private readonly ConcurrentDictionary<string, PlaybackProgress> _latest = new();
    // 마지막 flush 이후 갱신된 macroId들(dirty). 깨끗하면 재전송하지 않는다.
    private readonly ConcurrentDictionary<string, byte> _dirty = new();

    private volatile bool _disposed;

    public ProgressBroadcaster(SocketHub hub)
    {
        _hub = hub;
        _timer = new System.Threading.Timer(_ => Flush(), null, FlushIntervalMs, FlushIntervalMs);
    }

    /// <summary>매 raw Progress마다 호출(저렴). 최신 슬롯을 덮어쓰고 dirty 표시만 한다.</summary>
    public void Report(string macroId, PlaybackProgress p)
    {
        if (_disposed) return;
        _latest[macroId] = p;
        _dirty[macroId] = 0;
    }

    /// <summary>매크로 정지 시 호출 — 마지막 프레임을 1회 강제 전송하고 슬롯을 제거한다.
    /// (이전 프레임이 합쳐짐/드롭되어도 최종 프레임은 반드시 전달된다.)</summary>
    public void Complete(string macroId)
    {
        _dirty.TryRemove(macroId, out _);
        if (_latest.TryRemove(macroId, out var p))
            Send(macroId, p);
    }

    private void Flush()
    {
        if (_disposed) return;
        // dirty 표시된 것만 스냅샷해 전송. 같은 슬롯이 도중에 또 갱신되면 다음 tick에 다시 잡힌다.
        foreach (var macroId in _dirty.Keys)
        {
            if (!_dirty.TryRemove(macroId, out _)) continue;
            if (_latest.TryGetValue(macroId, out var p))
                Send(macroId, p);
        }
    }

    private void Send(string macroId, PlaybackProgress p) =>
        _hub.Broadcast("progress", new
        {
            macroId,
            loop = p.Loop,
            stepIndex = p.StepIndex,
            stepCount = p.StepCount,
            delayMs = p.DelayMs, // 현재 지연의 실제 대기(ms) — 채움 애니메이션 길이
            loops = p.Loops,     // [{ startIndex, total, remaining }] — 반복 진행
        });

    public void Dispose()
    {
        _disposed = true;
        _timer.Dispose();
    }
}
