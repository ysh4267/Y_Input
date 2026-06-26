using System.Diagnostics;
using YInput.Core.Models;
using YInput.Input;

namespace YInput.Engine;

/// <summary>녹화 옵션 — 어떤 입력을 기록할지, 지연을 고정할지.</summary>
public sealed record RecordOptions(
    bool Keyboard = true,
    bool MouseButtons = true,
    bool MouseMove = false,
    bool MouseWheel = true,
    bool Gamepad = false,
    double? FixedDelayMs = null)
{
    public static RecordOptions Default { get; } = new();
}

/// <summary>
/// <see cref="IInputSource"/>에서 캡처되는 입력을 타임스탬프와 함께 매크로로 기록한다.
/// 첫 스텝의 지연은 0, 이후는 직전(기록된) 이벤트와의 실제 간격(ms)으로 채운다.
/// <see cref="RecordOptions"/>로 기록 대상·지연 모드를 제어한다.
/// </summary>
public sealed class Recorder
{
    private readonly IInputSource _source;
    private readonly Stopwatch _clock = new();
    private readonly object _gate = new();

    private List<MacroStep>? _steps;
    private double _lastMs;
    private RecordOptions _options = RecordOptions.Default;

    public bool IsRecording { get; private set; }

    /// <summary>스텝이 기록될 때마다 발생(실시간 UI 갱신용).</summary>
    public event EventHandler<MacroStep>? StepRecorded;

    public Recorder(IInputSource source) => _source = source;

    public void Start() => Start(RecordOptions.Default);

    public void Start(RecordOptions options)
    {
        lock (_gate)
        {
            if (IsRecording) return;
            _options = options;
            _steps = new List<MacroStep>();
            _lastMs = 0;
            _clock.Restart();
            _source.Captured += OnCaptured;
            _source.StartCapture();
            IsRecording = true;
        }
    }

    /// <summary>옵션에 따라 이 이벤트를 기록할지 결정.</summary>
    private bool ShouldRecord(InputEvent e) => e switch
    {
        KeyboardEvent => _options.Keyboard,
        MouseEvent me => MouseEvents.Classify(me) switch
        {
            MouseEventKind.Move => _options.MouseMove,
            MouseEventKind.Button => _options.MouseButtons,
            MouseEventKind.Wheel => _options.MouseWheel,
            _ => true,
        },
        GamepadEvent => _options.Gamepad,
        _ => true,
    };

    private void OnCaptured(object? sender, InputEvent e)
    {
        lock (_gate)
        {
            if (!IsRecording || _steps is null) return;
            if (!ShouldRecord(e)) return; // 필터된 이벤트는 _lastMs도 갱신하지 않음(다음 스텝 지연에 누적)

            double now = _clock.Elapsed.TotalMilliseconds;
            double delay = _steps.Count == 0
                ? 0
                : (_options.FixedDelayMs ?? (now - _lastMs));
            _lastMs = now;

            var step = new MacroStep(e, delay);
            _steps.Add(step);
            StepRecorded?.Invoke(this, step);
        }
    }

    /// <summary>녹화를 끝내고 매크로를 만들어 반환한다.</summary>
    public Macro Stop(string name)
    {
        lock (_gate)
        {
            _source.StopCapture();
            _source.Captured -= OnCaptured;
            _clock.Stop();
            IsRecording = false;

            var macro = new Macro { Name = name, Steps = _steps ?? new List<MacroStep>() };
            _steps = null;
            return macro;
        }
    }
}
