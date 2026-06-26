using System.Text.Json.Serialization;

namespace YInput.Core.Models;

/// <summary>
/// 매크로 한 스텝이 발생시키는 입력 신호. 드라이버 백엔드가 이를 실제 입력으로 변환한다.
/// 직렬화 시 <c>$type</c> 판별자로 구체 타입을 구분한다(System.Text.Json polymorphism).
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(KeyboardEvent), "keyboard")]
[JsonDerivedType(typeof(MouseEvent), "mouse")]
[JsonDerivedType(typeof(GamepadEvent), "gamepad")]
[JsonDerivedType(typeof(TextEvent), "text")]
[JsonDerivedType(typeof(DelayEvent), "delay")]
[JsonDerivedType(typeof(LoopStartEvent), "loopStart")]
[JsonDerivedType(typeof(LoopEndEvent), "loopEnd")]
public abstract class InputEvent
{
    /// <summary>사람이 읽을 수 있는 요약(에디터 표시용).</summary>
    [JsonIgnore]
    public abstract string Summary { get; }
}

/// <summary>
/// 키보드 스트로크. Interception 드라이버의 KeyStroke를 그대로 미러링한다(스캔코드 기반).
/// </summary>
public sealed class KeyboardEvent : InputEvent
{
    /// <summary>키보드 스캔코드(set 1). 예: Esc=1, A=30.</summary>
    public ushort Code { get; set; }

    /// <summary>
    /// 키 상태 플래그. Interception KeyState와 동일:
    /// Down=0x00, Up=0x01, E0=0x02, E1=0x04 (조합 가능).
    /// </summary>
    public ushort State { get; set; }

    [JsonIgnore]
    public bool IsKeyUp => (State & 0x01) != 0;

    [JsonIgnore]
    public override string Summary => $"Key sc={Code:X2} {(IsKeyUp ? "up" : "down")}";
}

/// <summary>
/// 마우스 스트로크. Interception 드라이버의 MouseStroke를 그대로 미러링한다.
/// </summary>
public sealed class MouseEvent : InputEvent
{
    /// <summary>버튼/스크롤 상태 비트(Interception MouseState).</summary>
    public ushort ButtonState { get; set; }

    /// <summary>이동 플래그(Interception MouseFlags). 0=상대이동, 1=절대이동 등.</summary>
    public ushort Flags { get; set; }

    /// <summary>스크롤 휠 양(ScrollVertical/Horizontal 상태일 때).</summary>
    public short Rolling { get; set; }

    /// <summary>X 좌표 또는 상대 X 이동량.</summary>
    public int X { get; set; }

    /// <summary>Y 좌표 또는 상대 Y 이동량.</summary>
    public int Y { get; set; }

    [JsonIgnore]
    public override string Summary =>
        $"Mouse st={ButtonState:X4} fl={Flags:X2} d=({X},{Y}) roll={Rolling}";
}

/// <summary>가상 게임패드(ViGEm Xbox360) 단일 컨트롤 변경.</summary>
public sealed class GamepadEvent : InputEvent
{
    public GamepadControl Control { get; set; }

    /// <summary>
    /// 컨트롤 값. 버튼=0/1, 스틱 축=-32768..32767, 트리거=0..255.
    /// </summary>
    public int Value { get; set; }

    [JsonIgnore]
    public override string Summary => $"Pad {Control}={Value}";
}

/// <summary>
/// 문자열 타이핑(편집 편의용). 백엔드가 레이아웃에 맞춰 스캔코드 시퀀스로 변환해 입력한다.
/// 녹화는 <see cref="KeyboardEvent"/>를 생성하고, 이 타입은 수동 작성 시 사용한다.
/// </summary>
public sealed class TextEvent : InputEvent
{
    public string Text { get; set; } = string.Empty;

    /// <summary>각 키 누름 사이 지연(ms).</summary>
    public int PerKeyDelayMs { get; set; } = 0;

    [JsonIgnore]
    public override string Summary =>
        $"Type \"{(Text.Length > 24 ? Text[..24] + "…" : Text)}\"";
}

/// <summary>
/// 명시적 대기 스텝(no-op). 실제 대기 시간은 <see cref="MacroStep.DelayBeforeMs"/>가 담당하고,
/// 이 이벤트는 송출 시 아무 동작도 하지 않는다(편집기에서 "Wait" 행으로 표시).
/// </summary>
public sealed class DelayEvent : InputEvent
{
    [JsonIgnore]
    public override string Summary => "Wait";
}

/// <summary>
/// 반복 시작 블록(no-op 송출). 이 블록과 짝이 되는 <see cref="LoopEndEvent"/> 사이의 스텝을
/// <see cref="Count"/>회 반복한다. 중첩 가능(스택 매칭). 짝이 없으면 무시(그레이스풀).
/// </summary>
public sealed class LoopStartEvent : InputEvent
{
    /// <summary>반복 횟수(최소 1).</summary>
    public int Count { get; set; } = 2;

    [JsonIgnore]
    public override string Summary => $"Loop ×{Count}";
}

/// <summary>반복 끝 블록(no-op 송출). 가장 가까운 미닫힌 <see cref="LoopStartEvent"/>와 짝.</summary>
public sealed class LoopEndEvent : InputEvent
{
    [JsonIgnore]
    public override string Summary => "Loop end";
}
