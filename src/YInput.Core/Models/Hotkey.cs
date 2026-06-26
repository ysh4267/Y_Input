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

    /// <summary>Win32 Virtual-Key 코드(VK_*). 예: F8 = 0x77. 마우스 트리거면 무시.</summary>
    public uint VirtualKey { get; set; }

    /// <summary>설정 시 마우스 버튼 트리거. null이면 키보드 트리거.</summary>
    public MouseTriggerButton? Mouse { get; set; }

    public bool IsMouse => Mouse is not null;
    public bool IsEmpty => Mouse is null && VirtualKey == 0;

    /// <summary>"Ctrl+Alt+F8" / "Mouse X1" 형태의 표시 문자열.</summary>
    public override string ToString()
    {
        if (IsEmpty) return "(none)";
        var parts = new List<string>(4);
        if (Ctrl) parts.Add("Ctrl");
        if (Alt) parts.Add("Alt");
        if (Shift) parts.Add("Shift");
        if (Win) parts.Add("Win");
        parts.Add(IsMouse ? MouseName(Mouse!.Value) : KeyName.FromVirtualKey(VirtualKey));
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
