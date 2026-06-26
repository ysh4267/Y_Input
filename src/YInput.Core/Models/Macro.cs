namespace YInput.Core.Models;

/// <summary>녹화/편집/재생되는 입력 매크로.</summary>
public sealed class Macro
{
    /// <summary>고유 식별자(파일명에도 사용).</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Name { get; set; } = "Untitled";

    public List<MacroStep> Steps { get; set; } = new();

    /// <summary>반복 횟수. 0 이하면 무한 반복(정지 전까지).</summary>
    public int LoopCount { get; set; } = 1;

    /// <summary>재생 속도 배율. 1.0=원속도, 2.0=2배 빠름. 0 이하 금지.</summary>
    public double SpeedMultiplier { get; set; } = 1.0;

    /// <summary>이 매크로를 시작/정지하는 전역 핫키(선택).</summary>
    public Hotkey? Trigger { get; set; }

    /// <summary>마지막 수정 시각(UTC, ISO8601). 호스트가 저장 시 설정.</summary>
    public DateTimeOffset ModifiedUtc { get; set; }

    public bool IsInfinite => LoopCount <= 0;
}
