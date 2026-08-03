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
[JsonDerivedType(typeof(MacroRefEvent), "macroRef")]
[JsonDerivedType(typeof(PositionCorrectEvent), "positionCorrect")]
[JsonDerivedType(typeof(RuneUseEvent), "runeUse")]
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
    /// <summary>이 지연에만 적용되는 휴머나이즈(±%) 무작위 흔들림(0=없음). 재생 시 Player가 사용.</summary>
    public int RandomizePercent { get; set; } = 0;

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

/// <summary>
/// 위치 보정 블록 — 재생이 이 스텝에 도달하면 캐릭터가 저장된 자리(미니맵 점 + 기준 화면)에서
/// 벗어났는지 확인하고 방향키로 되돌린 뒤 다음 스텝으로 진행한다. 실제 보정은 Host의
/// PositionWatcher가 수행하며(Player에 훅으로 주입), 기준 위치가 저장돼 있지 않으면 no-op.
/// </summary>
public sealed class PositionCorrectEvent : InputEvent
{
    /// <summary>이 블록이 참조하는 기준 위치(스팟) id. 블록마다 서로 다른 자리를 가진다.
    /// Host가 spots\{id}.json/png로 저장·해석하며, 비어 있으면 보정은 no-op.</summary>
    public string SpotId { get; set; } = string.Empty;

    [JsonIgnore]
    public override string Summary => "위치 보정";
}

/// <summary>
/// 룬 사용 블록 — 재생이 이 스텝에 도달하면 미니맵의 룬(보라 다이아) 아이콘 위치로 캐릭터를
/// 이동시키고(좌우 걷기 + 윗점프/아래점프) 스페이스로 룬을 발동한 뒤, 화면의 방향키 퍼즐을
/// 인식해 자동 입력한다. 실제 수행은 Host의 PositionWatcher(Player에 훅으로 주입).
/// 룬이 미니맵에 없으면 no-op으로 다음 스텝 진행.
/// </summary>
public sealed class RuneUseEvent : InputEvent
{
    [JsonIgnore]
    public override string Summary => "룬 사용";
}

/// <summary>
/// 다른 매크로를 한 사이클 실행하는 블록(매크로 참조). 재생 시 <see cref="MacroId"/> 매크로의
/// 스텝들을 그 자리에 인라인 전개해 실행한다(순환은 무시). 반복이 필요하면 반복 블록으로 감싼다.
/// </summary>
public sealed class MacroRefEvent : InputEvent
{
    /// <summary>실행할 대상 매크로의 Id.</summary>
    public string MacroId { get; set; } = string.Empty;

    /// <summary>표시용 캐시 이름(편집기 렌더용, 실행에는 사용 안 함).</summary>
    public string Name { get; set; } = string.Empty;

    [JsonIgnore]
    public override string Summary => $"Run macro {(string.IsNullOrEmpty(Name) ? MacroId : Name)}";
}
