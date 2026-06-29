namespace YInput.Core.Models;

/// <summary>트리거로 쓸 수 있는 마우스 버튼(엄지 사이드 버튼 X1/X2 포함).</summary>
public enum MouseTriggerButton
{
    Left,
    Right,
    Middle,
    X1, // 엄지 뒤로 버튼
    X2, // 엄지 앞으로 버튼
}

/// <summary>
/// 전역 핫키 정의. 키보드(Win32 RegisterHotKey) 또는 마우스 버튼(WH_MOUSE_LL) 트리거.
/// <see cref="Mouse"/>가 설정되면 마우스 트리거이고, 그렇지 않으면 <see cref="VirtualKey"/> 키보드 트리거다.
/// </summary>
public sealed class Hotkey
{
    public bool Ctrl { get; set; }
    public bool Alt { get; set; }
    public bool Shift { get; set; }
    public bool Win { get; set; }

    /// <summary>Win32 Virtual-Key 코드(VK_*). 예: F8 = 0x77. 마우스 트리거면 무시. 단일 키 트리거에 사용.</summary>
    public uint VirtualKey { get; set; }

    /// <summary>
    /// 키보드 조합(chord) 트리거: 동시에 눌러야 발동하는 가상 키 집합(보통 2개 이상).
    /// 비어 있으면 단일 <see cref="VirtualKey"/>를 사용한다(하위 호환).
    /// </summary>
    public List<uint> Keys { get; set; } = new();

    /// <summary>설정 시 마우스 버튼 트리거. null이면 키보드 트리거.</summary>
    public MouseTriggerButton? Mouse { get; set; }

    /// <summary>설정 시 게임패드 버튼/트리거 트리거(우선순위 최상).</summary>
    public GamepadControl? Gamepad { get; set; }

    public bool IsGamepad => Gamepad is not null;
    public bool IsMouse => !IsGamepad && Mouse is not null;
    public bool IsEmpty => Gamepad is null && Mouse is null && VirtualKey == 0 && (Keys is null || Keys.Count == 0);

    /// <summary>키보드 조합(2개 이상 키 동시) 트리거인지.</summary>
    public bool IsKeyChord => !IsGamepad && !IsMouse && Keys is { Count: >= 2 };

    /// <summary>키보드 트리거가 실제로 사용할 키 목록(조합이면 Keys, 아니면 VirtualKey 단일).</summary>
    public IReadOnlyList<uint> EffectiveKeys =>
        Keys is { Count: > 0 } ? Keys : (VirtualKey != 0 ? new List<uint> { VirtualKey } : new List<uint>());

    /// <summary>"Ctrl+Alt+F8" / "Mouse X1" / "Pad A" 형태의 표시 문자열.</summary>
    public override string ToString()
    {
        if (IsEmpty) return "(none)";
        var parts = new List<string>(4);
        if (Ctrl) parts.Add("Ctrl");
        if (Alt) parts.Add("Alt");
        if (Shift) parts.Add("Shift");
        if (Win) parts.Add("Win");
        if (IsGamepad) parts.Add($"Pad {Gamepad}");
        else if (IsMouse) parts.Add(MouseName(Mouse!.Value));
        else if (Keys is { Count: > 0 }) parts.AddRange(Keys.Select(KeyName.FromVirtualKey));
        else parts.Add(KeyName.FromVirtualKey(VirtualKey));
        return string.Join("+", parts);
    }

    public static string MouseName(MouseTriggerButton b) => b switch
    {
        MouseTriggerButton.Left => "Mouse좌",
        MouseTriggerButton.Right => "Mouse우",
        MouseTriggerButton.Middle => "Mouse휠",
        MouseTriggerButton.X1 => "Mouse X1(엄지뒤로)",
        MouseTriggerButton.X2 => "Mouse X2(엄지앞으로)",
        _ => "Mouse?",
    };
}

/// <summary>가상 키 코드 → 표시 이름(요약용 최소 매핑).</summary>
public static class KeyName
{
    public static string FromVirtualKey(uint vk) => vk switch
    {
        >= 0x70 and <= 0x87 => $"F{vk - 0x6F}",          // F1..F24
        >= 0x30 and <= 0x39 => ((char)vk).ToString(),    // 0..9
        >= 0x41 and <= 0x5A => ((char)vk).ToString(),    // A..Z
        0x20 => "Space",
        0x0D => "Enter",
        0x1B => "Esc",
        _ => $"VK_0x{vk:X2}",
    };
}
