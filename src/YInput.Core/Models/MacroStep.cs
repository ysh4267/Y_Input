namespace YInput.Core.Models;

/// <summary>매크로의 한 스텝: 입력 이벤트 + 직전 대기 시간.</summary>
public sealed class MacroStep
{
    /// <summary>이 스텝을 실행하기 전 대기할 시간(ms). 녹화 시 이벤트 간 간격으로 채워진다.</summary>
    public double DelayBeforeMs { get; set; }

    public InputEvent Event { get; set; } = default!;

    public MacroStep() { }

    public MacroStep(InputEvent @event, double delayBeforeMs = 0)
    {
        Event = @event;
        DelayBeforeMs = delayBeforeMs;
    }
}
