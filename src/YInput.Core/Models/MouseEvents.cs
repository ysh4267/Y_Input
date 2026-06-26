namespace YInput.Core.Models;

public enum MouseEventKind
{
    Move,
    Button,
    Wheel,
}

/// <summary>마우스 이벤트 분류 헬퍼(녹화 필터·편집기 표시에 재사용).</summary>
public static class MouseEvents
{
    // Interception MouseState 비트
    private const ushort ButtonMask = 0x03FF; // 버튼 down/up 비트(좌·우·중·확장1·2)
    private const ushort ScrollMask = 0x0C00; // ScrollVertical(0x400) | ScrollHorizontal(0x800)

    public static MouseEventKind Classify(MouseEvent e)
    {
        if ((e.ButtonState & ScrollMask) != 0 || e.Rolling != 0)
            return MouseEventKind.Wheel;
        if ((e.ButtonState & ButtonMask) != 0)
            return MouseEventKind.Button;
        return MouseEventKind.Move;
    }
}
