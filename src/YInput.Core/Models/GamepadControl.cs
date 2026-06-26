namespace YInput.Core.Models;

/// <summary>
/// 가상 게임패드(Xbox360 호환)에서 제어 가능한 컨트롤.
/// 백엔드(ViGEm)가 각 항목을 버튼/축/트리거로 분류해 매핑한다.
/// </summary>
public enum GamepadControl
{
    // 버튼 (Value: 0=뗌, 1=누름)
    A,
    B,
    X,
    Y,
    LeftShoulder,
    RightShoulder,
    Back,
    Start,
    Guide,
    LeftThumb,
    RightThumb,
    DpadUp,
    DpadDown,
    DpadLeft,
    DpadRight,

    // 아날로그 스틱 축 (Value: -32768..32767)
    LeftStickX,
    LeftStickY,
    RightStickX,
    RightStickY,

    // 트리거 (Value: 0..255)
    LeftTrigger,
    RightTrigger,
}

/// <summary>게임패드 컨트롤 분류 헬퍼.</summary>
public static class GamepadControls
{
    public static GamepadControlKind KindOf(GamepadControl control) => control switch
    {
        GamepadControl.LeftStickX or GamepadControl.LeftStickY or
        GamepadControl.RightStickX or GamepadControl.RightStickY => GamepadControlKind.Axis,

        GamepadControl.LeftTrigger or GamepadControl.RightTrigger => GamepadControlKind.Trigger,

        _ => GamepadControlKind.Button,
    };
}

public enum GamepadControlKind
{
    Button,
    Axis,
    Trigger,
}
