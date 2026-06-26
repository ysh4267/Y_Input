using System.Runtime.InteropServices;

namespace YInput.Engine;

public enum RawInputKind { Keyboard, Mouse, Hid }

/// <summary>인식된 입력 한 건.</summary>
public sealed class DetectedInput
{
    public RawInputKind Kind { get; init; }
    public string Label { get; init; } = "";
    public uint VirtualKey { get; init; }   // 키보드일 때
    public int RawBytes { get; init; }       // HID일 때 보고 바이트 수
}

/// <summary>
/// Raw Input(WM_INPUT)으로 키보드·마우스·HID(게임패드/조이스틱 포함) 입력을 인식한다.
/// 자체 숨김 창 + 메시지 루프에서 RIDEV_INPUTSINK로 백그라운드에서도 수신한다.
/// 의미 있는 입력(키 다운, 마우스 버튼/휠, HID 보고)만 <see cref="Detected"/>로 알린다.
/// </summary>
public sealed class RawInputMonitor : IDisposable
{
    private const uint WM_INPUT = 0x00FF;
    private const uint WM_DESTROY = 0x0002, WM_CLOSE = 0x0010;
    private const uint RID_INPUT = 0x10000003;
    private const uint RIDEV_INPUTSINK = 0x00000100, RIDEV_REMOVE = 0x00000001;
    private const uint RIM_TYPEMOUSE = 0, RIM_TYPEKEYBOARD = 1, RIM_TYPEHID = 2;
    private const ushort RI_KEY_BREAK = 0x01;

    private readonly string _className = "YInputRawWnd_" + Guid.NewGuid().ToString("N");
    private readonly ManualResetEventSlim _ready = new(false);
    private Thread? _thread;
    private IntPtr _hwnd;
    private WndProc? _wndProc;
    private volatile bool _running;
    private int _headerSize;

    public bool IsEnabled { get; private set; }
    public event EventHandler<DetectedInput>? Detected;

    public RawInputMonitor()
    {
        _headerSize = Marshal.SizeOf<RAWINPUTHEADER>();
        _thread = new Thread(ThreadMain) { IsBackground = true, Name = "YInput-RawInput" };
        _thread.Start();
        _ready.Wait(3000);
    }

    public void Enable()
    {
        if (IsEnabled || _hwnd == IntPtr.Zero) return;
        var devs = new[]
        {
            NewDev(0x01, 0x06), // 키보드
            NewDev(0x01, 0x02), // 마우스
            NewDev(0x01, 0x05), // 게임패드
            NewDev(0x01, 0x04), // 조이스틱
        };
        if (RegisterRawInputDevices(devs, (uint)devs.Length, (uint)Marshal.SizeOf<RAWINPUTDEVICE>()))
            IsEnabled = true;
    }

    public void Disable()
    {
        if (!IsEnabled) return;
        var devs = new[]
        {
            RemoveDev(0x01, 0x06), RemoveDev(0x01, 0x02), RemoveDev(0x01, 0x05), RemoveDev(0x01, 0x04),
        };
        RegisterRawInputDevices(devs, (uint)devs.Length, (uint)Marshal.SizeOf<RAWINPUTDEVICE>());
        IsEnabled = false;
    }

    private RAWINPUTDEVICE NewDev(ushort page, ushort usage) =>
        new() { usUsagePage = page, usUsage = usage, dwFlags = RIDEV_INPUTSINK, hwndTarget = _hwnd };
    private static RAWINPUTDEVICE RemoveDev(ushort page, ushort usage) =>
        new() { usUsagePage = page, usUsage = usage, dwFlags = RIDEV_REMOVE, hwndTarget = IntPtr.Zero };

    private void ThreadMain()
    {
        IntPtr hInstance = GetModuleHandle(null);
        _wndProc = WndProcImpl;
        var wc = new WNDCLASSEX
        {
            cbSize = Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = hInstance,
            lpszClassName = _className,
        };
        RegisterClassEx(ref wc);
        // 숨김 일반 창(INPUTSINK는 message-only 창에선 동작 안 함)
        _hwnd = CreateWindowEx(0, _className, string.Empty, 0, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero);
        _running = true;
        _ready.Set();

        while (_running && GetMessage(out MSG msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }
    }

    private IntPtr WndProcImpl(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_INPUT)
        {
            try { HandleRawInput(lParam); } catch { /* 무시 */ }
            return DefWindowProc(hWnd, msg, wParam, lParam);
        }
        if (msg == WM_DESTROY) { PostQuitMessage(0); return IntPtr.Zero; }
        return DefWindowProc(hWnd, msg, wParam, lParam);
    }

    private void HandleRawInput(IntPtr hRawInput)
    {
        uint size = 0;
        GetRawInputData(hRawInput, RID_INPUT, IntPtr.Zero, ref size, (uint)_headerSize);
        if (size == 0) return;

        IntPtr buf = Marshal.AllocHGlobal((int)size);
        try
        {
            if (GetRawInputData(hRawInput, RID_INPUT, buf, ref size, (uint)_headerSize) != size) return;
            var header = Marshal.PtrToStructure<RAWINPUTHEADER>(buf);
            IntPtr body = buf + _headerSize;

            if (header.dwType == RIM_TYPEKEYBOARD)
            {
                var kb = Marshal.PtrToStructure<RAWKEYBOARD>(body);
                if ((kb.Flags & RI_KEY_BREAK) == 0 && kb.VKey != 0 && kb.VKey < 0xFF)
                    Raise(RawInputKind.Keyboard, VkName(kb.VKey), kb.VKey, 0);
            }
            else if (header.dwType == RIM_TYPEMOUSE)
            {
                var m = Marshal.PtrToStructure<RAWMOUSE>(body);
                string? name = m.usButtonFlags switch
                {
                    var f when (f & 0x0001) != 0 => "좌클릭",
                    var f when (f & 0x0004) != 0 => "우클릭",
                    var f when (f & 0x0010) != 0 => "휠클릭",
                    var f when (f & 0x0040) != 0 => "X1(엄지뒤로)",
                    var f when (f & 0x0100) != 0 => "X2(엄지앞으로)",
                    var f when (f & 0x0400) != 0 => "휠",
                    _ => null, // 단순 이동은 무시
                };
                if (name != null) Raise(RawInputKind.Mouse, name, 0, 0);
            }
            else if (header.dwType == RIM_TYPEHID)
            {
                var hid = Marshal.PtrToStructure<RAWHID>(body);
                Raise(RawInputKind.Hid, "HID 입력", 0, (int)(hid.dwSizeHid * hid.dwCount));
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
    }

    private void Raise(RawInputKind kind, string label, uint vk, int rawBytes) =>
        Detected?.Invoke(this, new DetectedInput { Kind = kind, Label = label, VirtualKey = vk, RawBytes = rawBytes });

    private static string VkName(ushort vk) => vk switch
    {
        >= 0x70 and <= 0x87 => $"F{vk - 0x6F}",
        >= 0x30 and <= 0x5A => ((char)vk).ToString(),
        0x20 => "Space", 0x0D => "Enter", 0x1B => "Esc", 0x08 => "Backspace", 0x09 => "Tab",
        _ => $"VK_0x{vk:X2}",
    };

    public void Dispose()
    {
        try { Disable(); } catch { }
        if (_hwnd != IntPtr.Zero)
        {
            _running = false;
            PostMessage(_hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        }
        _thread?.Join(2000);
        _ready.Dispose();
    }

    // ---- Win32 ----
    private delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTDEVICE { public ushort usUsagePage; public ushort usUsage; public uint dwFlags; public IntPtr hwndTarget; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTHEADER { public uint dwType; public uint dwSize; public IntPtr hDevice; public IntPtr wParam; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWKEYBOARD { public ushort MakeCode; public ushort Flags; public ushort Reserved; public ushort VKey; public uint Message; public uint ExtraInformation; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWMOUSE
    {
        public ushort usFlags;
        public ushort _pad;
        public ushort usButtonFlags;
        public ushort usButtonData;
        public uint ulRawButtons;
        public int lLastX;
        public int lLastY;
        public uint ulExtraInformation;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWHID { public uint dwSizeHid; public uint dwCount; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public int cbSize; public uint style; public IntPtr lpfnWndProc;
        public int cbClsExtra; public int cbWndExtra; public IntPtr hInstance;
        public IntPtr hIcon; public IntPtr hCursor; public IntPtr hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam; public IntPtr lParam; public uint time; public int pt_x; public int pt_y; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ushort RegisterClassEx(ref WNDCLASSEX lpwcx);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(uint dwExStyle, string lpClassName, string lpWindowName, uint dwStyle, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr hInstance, IntPtr lpParam);
    [DllImport("user32.dll")] private static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref MSG lpMsg);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessage(ref MSG lpMsg);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern void PostQuitMessage(int exitCode);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterRawInputDevices([In] RAWINPUTDEVICE[] pRawInputDevices, uint uiNumDevices, uint cbSize);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint GetRawInputData(IntPtr hRawInput, uint uiCommand, IntPtr pData, ref uint pcbSize, uint cbSizeHeader);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? lpModuleName);
}
