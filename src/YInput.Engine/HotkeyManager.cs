using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using YInput.Core.Models;

namespace YInput.Engine;

/// <summary>
/// 전역 핫키 등록/해제. 자체 스레드에서 message-only 윈도우와 메시지 루프를 돌리고,
/// Win32 <c>RegisterHotKey</c>로 등록한다. WM_HOTKEY 수신 시 등록된 콜백을 호출한다.
/// (RegisterHotKey는 메시지 루프 소유 스레드에서 호출해야 하므로 모든 등록을 그 스레드로 마샬링한다.)
/// </summary>
public sealed class HotkeyManager : IDisposable
{
    private const uint WM_HOTKEY = 0x0312;
    private const uint WM_APP = 0x8000;
    private const uint WM_CLOSE = 0x0010;
    private const uint WM_DESTROY = 0x0002;
    private static readonly IntPtr HWND_MESSAGE = new(-3);

    private const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_WIN = 0x8, MOD_NOREPEAT = 0x4000;

    // 저수준 마우스 훅(WH_MOUSE_LL) — RegisterHotKey가 지원하지 않는 마우스 버튼 트리거용
    private const int WH_MOUSE_LL = 14;
    private const uint WM_LBUTTONDOWN = 0x0201, WM_RBUTTONDOWN = 0x0204, WM_MBUTTONDOWN = 0x0207, WM_XBUTTONDOWN = 0x020B;
    private const uint LLMHF_INJECTED = 0x00000001;
    private const int VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12, VK_LWIN = 0x5B, VK_RWIN = 0x5C;

    // 저수준 키보드 훅(WH_KEYBOARD_LL) — RegisterHotKey가 못 하는 "2개 이상 일반 키 동시(chord)" 트리거용
    private const int WH_KEYBOARD_LL = 13;
    private const uint WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101, WM_SYSKEYDOWN = 0x0104, WM_SYSKEYUP = 0x0105;
    private const uint LLKHF_INJECTED = 0x00000010;

    private readonly Dictionary<int, Action> _callbacks = new();
    private readonly Dictionary<int, (MouseTriggerButton button, Hotkey hk, Action cb)> _mouseTriggers = new();
    private readonly Dictionary<int, KeyChord> _keyChords = new();
    private readonly HashSet<uint> _heldKeys = new(); // 키보드 훅으로 추적하는 현재 눌린 키(주입 입력 제외)
    private LowLevelMouseProc? _mouseProc; // GC 방지
    private IntPtr _mouseHook;
    private LowLevelKeyboardProc? _keyboardProc; // GC 방지
    private IntPtr _keyboardHook;

    /// <summary>키보드 조합(chord) 트리거 1건. 모든 키가 동시에 눌린 순간 1회 발동(라이징 엣지).</summary>
    private sealed class KeyChord
    {
        public uint[] Keys = Array.Empty<uint>();
        public Hotkey Hk = null!;
        public Action Cb = static () => { };
        public bool Active; // 직전에 조합이 완성돼 있었는지(연타·반복 방지)
    }
    private readonly ConcurrentQueue<Action> _queue = new();
    private readonly ManualResetEventSlim _ready = new(false);
    private readonly string _className = "YInputHotkeyWnd_" + Guid.NewGuid().ToString("N");

    private Thread? _thread;
    private IntPtr _hwnd;
    private WndProc? _wndProc; // GC 방지용 보관
    private int _nextId = 1;
    private volatile bool _running;

    /// <summary>핫키가 눌리면 등록 id와 함께 발생.</summary>
    public event EventHandler<int>? HotkeyPressed;

    public HotkeyManager()
    {
        _thread = new Thread(ThreadMain) { IsBackground = true, Name = "YInput-Hotkeys" };
        _thread.Start();
        _ready.Wait(3000);
    }

    /// <summary>핫키를 등록하고 id를 반환한다. 실패 시 예외.</summary>
    public int Register(Hotkey hk, Action callback)
    {
        if (hk.IsEmpty) throw new ArgumentException("빈 핫키는 등록할 수 없습니다.");
        return InvokeOnLoop(() =>
        {
            int id = _nextId++;
            uint mods = MOD_NOREPEAT
                        | (hk.Ctrl ? MOD_CONTROL : 0)
                        | (hk.Alt ? MOD_ALT : 0)
                        | (hk.Shift ? MOD_SHIFT : 0)
                        | (hk.Win ? MOD_WIN : 0);

            if (!RegisterHotKey(_hwnd, id, mods, hk.VirtualKey))
            {
                int err = Marshal.GetLastWin32Error();
                throw new InvalidOperationException(
                    $"핫키 등록 실패 ({hk}) — 다른 프로그램이 이미 사용 중일 수 있습니다 (Win32 0x{err:X}).");
            }
            _callbacks[id] = callback;
            return id;
        });
    }

    /// <summary>마우스 버튼 트리거를 등록한다(WH_MOUSE_LL). 첫 등록 시 훅을 설치한다.</summary>
    public int RegisterMouse(Hotkey hk, Action callback)
    {
        if (hk.Mouse is null) throw new ArgumentException("마우스 트리거가 아닙니다.");
        return InvokeOnLoop(() =>
        {
            EnsureMouseHook();
            int id = _nextId++;
            _mouseTriggers[id] = (hk.Mouse.Value, hk, callback);
            return id;
        });
    }

    private void EnsureMouseHook()
    {
        if (_mouseHook != IntPtr.Zero) return;
        _mouseProc = MouseHookProc;
        _mouseHook = SetWindowsHookEx(WH_MOUSE_LL, _mouseProc, GetModuleHandle(null), 0);
        if (_mouseHook == IntPtr.Zero)
            throw new InvalidOperationException($"마우스 훅 설치 실패 (Win32 0x{Marshal.GetLastWin32Error():X}).");
    }

    private void RemoveMouseHookIfIdle()
    {
        if (_mouseTriggers.Count == 0 && _mouseHook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_mouseHook);
            _mouseHook = IntPtr.Zero;
            _mouseProc = null;
        }
    }

    /// <summary>키보드 조합(2개 이상 키 동시) 트리거를 등록한다(WH_KEYBOARD_LL). 첫 등록 시 훅을 설치한다.</summary>
    public int RegisterKeyChord(Hotkey hk, Action callback)
    {
        var keys = hk.EffectiveKeys.ToArray();
        if (keys.Length == 0) throw new ArgumentException("키 조합이 비어 있습니다.");
        return InvokeOnLoop(() =>
        {
            EnsureKeyboardHook();
            int id = _nextId++;
            _keyChords[id] = new KeyChord { Keys = keys, Hk = hk, Cb = callback };
            return id;
        });
    }

    private void EnsureKeyboardHook()
    {
        if (_keyboardHook != IntPtr.Zero) return;
        _heldKeys.Clear();
        _keyboardProc = KeyboardHookProc;
        _keyboardHook = SetWindowsHookEx(WH_KEYBOARD_LL, _keyboardProc, GetModuleHandle(null), 0);
        if (_keyboardHook == IntPtr.Zero)
            throw new InvalidOperationException($"키보드 훅 설치 실패 (Win32 0x{Marshal.GetLastWin32Error():X}).");
    }

    private void RemoveKeyboardHookIfIdle()
    {
        if (_keyChords.Count == 0 && _keyboardHook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_keyboardHook);
            _keyboardHook = IntPtr.Zero;
            _keyboardProc = null;
            _heldKeys.Clear();
        }
    }

    public void Unregister(int id)
    {
        InvokeOnLoop<object?>(() =>
        {
            if (_callbacks.Remove(id))
                UnregisterHotKey(_hwnd, id);
            if (_mouseTriggers.Remove(id))
                RemoveMouseHookIfIdle();
            if (_keyChords.Remove(id))
                RemoveKeyboardHookIfIdle();
            return null;
        });
    }

    public void UnregisterAll()
    {
        InvokeOnLoop<object?>(() =>
        {
            foreach (var id in _callbacks.Keys.ToList())
                UnregisterHotKey(_hwnd, id);
            _callbacks.Clear();
            _mouseTriggers.Clear();
            RemoveMouseHookIfIdle();
            _keyChords.Clear();
            RemoveKeyboardHookIfIdle();
            return null;
        });
    }

    private T InvokeOnLoop<T>(Func<T> func)
    {
        if (_hwnd == IntPtr.Zero) throw new InvalidOperationException("핫키 루프가 준비되지 않았습니다.");
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _queue.Enqueue(() =>
        {
            try { tcs.SetResult(func()); }
            catch (Exception ex) { tcs.SetException(ex); }
        });
        PostMessage(_hwnd, WM_APP, IntPtr.Zero, IntPtr.Zero);
        return tcs.Task.GetAwaiter().GetResult();
    }

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

        _hwnd = CreateWindowEx(0, _className, string.Empty, 0, 0, 0, 0, 0,
            HWND_MESSAGE, IntPtr.Zero, hInstance, IntPtr.Zero);
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
        switch (msg)
        {
            case WM_HOTKEY:
                int id = wParam.ToInt32();
                if (_callbacks.TryGetValue(id, out var cb))
                {
                    try { cb(); } catch { /* 콜백 예외 무시 */ }
                }
                HotkeyPressed?.Invoke(this, id);
                return IntPtr.Zero;

            case WM_APP:
                while (_queue.TryDequeue(out var action)) action();
                return IntPtr.Zero;

            case WM_DESTROY:
                PostQuitMessage(0);
                return IntPtr.Zero;
        }
        return DefWindowProc(hWnd, msg, wParam, lParam);
    }

    private IntPtr MouseHookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && _mouseTriggers.Count > 0)
        {
            uint msg = (uint)wParam.ToInt32();
            var data = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
            if ((data.flags & LLMHF_INJECTED) == 0) // 주입된 입력(매크로 재생 등)은 무시
            {
                MouseTriggerButton? btn = msg switch
                {
                    WM_LBUTTONDOWN => MouseTriggerButton.Left,
                    WM_RBUTTONDOWN => MouseTriggerButton.Right,
                    WM_MBUTTONDOWN => MouseTriggerButton.Middle,
                    WM_XBUTTONDOWN => ((data.mouseData >> 16) & 0xFFFF) == 1 ? MouseTriggerButton.X1 : MouseTriggerButton.X2,
                    _ => (MouseTriggerButton?)null,
                };
                if (btn is not null)
                {
                    foreach (var (button, hk, cb) in _mouseTriggers.Values.ToList())
                    {
                        if (button == btn.Value && ModifiersHeld(hk))
                        {
                            try { cb(); } catch { /* 콜백 예외 무시 */ }
                        }
                    }
                }
            }
        }
        return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam); // 통과(차단 안 함)
    }

    private IntPtr KeyboardHookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && _keyChords.Count > 0)
        {
            var data = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            if ((data.flags & LLKHF_INJECTED) == 0) // 주입된 입력(매크로 재생 등)은 무시
            {
                uint msg = (uint)wParam.ToInt32();
                bool isDown = msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN;
                bool isUp = msg == WM_KEYUP || msg == WM_SYSKEYUP;
                if (isDown) _heldKeys.Add(data.vkCode);
                else if (isUp) _heldKeys.Remove(data.vkCode);

                if (isDown || isUp)
                {
                    // 조합의 모든 키가 눌려있고 모디파이어가 충족되면, "막 완성된 순간"에 1회 발동.
                    foreach (var chord in _keyChords.Values.ToList())
                    {
                        bool now = ModifiersHeld(chord.Hk) && Array.TrueForAll(chord.Keys, _heldKeys.Contains);
                        if (now && !chord.Active)
                        {
                            try { chord.Cb(); } catch { /* 콜백 예외 무시 */ }
                        }
                        chord.Active = now;
                    }
                }
            }
        }
        return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam); // 통과(차단 안 함)
    }

    private static bool ModifiersHeld(Hotkey hk)
    {
        static bool Down(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;
        if (hk.Ctrl && !Down(VK_CONTROL)) return false;
        if (hk.Alt && !Down(VK_MENU)) return false;
        if (hk.Shift && !Down(VK_SHIFT)) return false;
        if (hk.Win && !(Down(VK_LWIN) || Down(VK_RWIN))) return false;
        return true;
    }

    public void Dispose()
    {
        if (_hwnd != IntPtr.Zero)
        {
            try { UnregisterAll(); } catch { /* ignore */ }
            _running = false;
            PostMessage(_hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        }
        _thread?.Join(2000);
        _ready.Dispose();
    }

    // ---- Win32 ----
    private delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);
    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int x; public int y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public UIntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public UIntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public int cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int pt_x;
        public int pt_y;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassEx(ref WNDCLASSEX lpwcx);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(uint dwExStyle, string lpClassName, string lpWindowName,
        uint dwStyle, int x, int y, int nWidth, int nHeight, IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll")] private static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref MSG lpMsg);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessage(ref MSG lpMsg);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern void PostQuitMessage(int exitCode);

    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? lpModuleName);
}
