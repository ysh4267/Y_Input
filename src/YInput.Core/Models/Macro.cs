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

    /// <summary>
    /// 지연 무작위화(휴머나이즈) 비율(%). 0=없음. 각 스텝 지연에 ±이 비율만큼 무작위 지터를 적용한다.
    /// </summary>
    public int RandomizeDelayPercent { get; set; } = 0;

    /// <summary>이 매크로를 시작/정지하는 전역 핫키(선택).</summary>
    public Hotkey? Trigger { get; set; }

    /// <summary>적용(활성) 여부. 기본 false(꺼짐) — 실행 페이지에서 켠 것만 트리거 핫키가 무장된다.</summary>
    public bool Enabled { get; set; } = false;

    /// <summary>매크로 목록에서의 사용자 지정 정렬 순서(작을수록 위). 드래그로 변경·저장된다.</summary>
    public int Order { get; set; } = 0;

    /// <summary>마지막 수정 시각(UTC, ISO8601). 호스트가 저장 시 설정.</summary>
    public DateTimeOffset ModifiedUtc { get; set; }

    public bool IsInfinite => LoopCount <= 0;
}
