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

    private readonly Dictionary<int, Action> _callbacks = new();
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

    public void Unregister(int id)
    {
        InvokeOnLoop<object?>(() =>
        {
            if (_callbacks.Remove(id))
                UnregisterHotKey(_hwnd, id);
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
