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

/// <summary>Win32 가상 키 코드(VK_*) → 사람이 읽는 표시 이름. 클라이언트 keymap.js vkLabel과 1:1로 일치시킨다.</summary>
public static class KeyName
{
    public static string FromVirtualKey(uint vk) => vk switch
    {
        // 글자·숫자·기능키·넘패드 숫자 (범위)
        >= 0x41 and <= 0x5A => ((char)vk).ToString(),    // A..Z
        >= 0x30 and <= 0x39 => ((char)vk).ToString(),    // 0..9
        >= 0x70 and <= 0x87 => $"F{vk - 0x6F}",          // F1..F24
        >= 0x60 and <= 0x69 => $"Num{vk - 0x60}",        // 넘패드 0..9

        // 공백·편집·제어
        0x08 => "Backspace", 0x09 => "Tab", 0x0C => "Clear", 0x0D => "Enter",
        0x1B => "Esc", 0x20 => "Space", 0x03 => "Break", 0x13 => "Pause", 0x14 => "Caps",

        // 한글 IME
        0x15 => "한/영", 0x19 => "한자",

        // 이동·편집키
        0x21 => "PgUp", 0x22 => "PgDn", 0x23 => "End", 0x24 => "Home",
        0x25 => "←", 0x26 => "↑", 0x27 => "→", 0x28 => "↓",
        0x29 => "Select", 0x2C => "PrtSc", 0x2D => "Ins", 0x2E => "Del", 0x2F => "Help",

        // Win·메뉴·전원
        0x5B => "LWin", 0x5C => "RWin", 0x5D => "Menu", 0x5F => "Sleep",

        // 넘패드 연산자
        0x6A => "Num*", 0x6B => "Num+", 0x6C => "Num,", 0x6D => "Num-", 0x6E => "Num.", 0x6F => "Num/",

        // 토글
        0x90 => "NumLk", 0x91 => "ScrLk",

        // 좌우 구분 모디파이어
        0xA0 => "LShift", 0xA1 => "RShift", 0xA2 => "LCtrl", 0xA3 => "RCtrl", 0xA4 => "LAlt", 0xA5 => "RAlt",

        // 브라우저
        0xA6 => "브라우저 뒤로", 0xA7 => "브라우저 앞으로", 0xA8 => "새로고침", 0xA9 => "브라우저 정지",
        0xAA => "브라우저 검색", 0xAB => "즐겨찾기", 0xAC => "브라우저 홈",

        // 볼륨·미디어
        0xAD => "음소거", 0xAE => "볼륨 -", 0xAF => "볼륨 +",
        0xB0 => "다음 트랙", 0xB1 => "이전 트랙", 0xB2 => "미디어 정지", 0xB3 => "재생/일시정지",
        0xB4 => "메일", 0xB5 => "미디어 선택", 0xB6 => "앱1", 0xB7 => "앱2",

        // OEM 기호 (US 배열 기준)
        0xBA => ";", 0xBB => "=", 0xBC => ",", 0xBD => "-", 0xBE => ".", 0xBF => "/",
        0xC0 => "`", 0xDB => "[", 0xDC => "\\", 0xDD => "]", 0xDE => "'", 0xDF => "OEM8", 0xE2 => "\\",

        _ => $"키 0x{vk:X2}",
    };
}
