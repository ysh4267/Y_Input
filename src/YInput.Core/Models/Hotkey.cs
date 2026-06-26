namespace YInput.Core.Models;

/// <summary>
/// 전역 핫키 정의. Win32 RegisterHotKey 용 수정자 + 가상 키.
/// </summary>
public sealed class Hotkey
{
    public bool Ctrl { get; set; }
    public bool Alt { get; set; }
    public bool Shift { get; set; }
    public bool Win { get; set; }

    /// <summary>Win32 Virtual-Key 코드(VK_*). 예: F8 = 0x77.</summary>
    public uint VirtualKey { get; set; }

    public bool IsEmpty => VirtualKey == 0;

    /// <summary>"Ctrl+Alt+F8" 형태의 표시 문자열.</summary>
    public override string ToString()
    {
        if (IsEmpty) return "(none)";
        var parts = new List<string>(4);
        if (Ctrl) parts.Add("Ctrl");
        if (Alt) parts.Add("Alt");
        if (Shift) parts.Add("Shift");
        if (Win) parts.Add("Win");
        parts.Add(KeyName.FromVirtualKey(VirtualKey));
        return string.Join("+", parts);
    }
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
