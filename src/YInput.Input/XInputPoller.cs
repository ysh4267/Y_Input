using System.Runtime.InteropServices;
using YInput.Core.Models;

namespace YInput.Input;

/// <summary>XINPUT_GAMEPAD 미러(테스트·매핑용).</summary>
public struct XGamepad
{
    public ushort Buttons;
    public byte LeftTrigger;
    public byte RightTrigger;
    public short LX, LY, RX, RY;
}

/// <summary>
/// XInput으로 물리 Xbox 호환 컨트롤러(0~3) 입력을 폴링한다.
/// 변화가 있을 때만 <see cref="Changed"/>(GamepadEvent)를 발생시킨다(녹화·트리거·입력감지용).
/// </summary>
public sealed class XInputPoller : IDisposable
{
    // XInput 버튼 비트 → GamepadControl
    private static readonly (ushort bit, GamepadControl control)[] ButtonMap =
    {
        (0x1000, GamepadControl.A), (0x2000, GamepadControl.B),
        (0x4000, GamepadControl.X), (0x8000, GamepadControl.Y),
        (0x0100, GamepadControl.LeftShoulder), (0x0200, GamepadControl.RightShoulder),
        (0x0020, GamepadControl.Back), (0x0010, GamepadControl.Start),
        (0x0400, GamepadControl.Guide),
        (0x0040, GamepadControl.LeftThumb), (0x0080, GamepadControl.RightThumb),
        (0x0001, GamepadControl.DpadUp), (0x0002, GamepadControl.DpadDown),
        (0x0004, GamepadControl.DpadLeft), (0x0008, GamepadControl.DpadRight),
    };

    private const int StickDeadzone = 8000;
    private const int StickStep = 2048;
    private const int TriggerStep = 8;

    private Thread? _thread;
    private volatile bool _running;
    private readonly XGamepad[] _prev = new XGamepad[4];
    private readonly bool[] _connected = new bool[4];
    private readonly int[] _skip = new int[4];

    public event EventHandler<GamepadEvent>? Changed;
    public bool AnyConnected { get; private set; }

    public void Start()
    {
        if (_running) return;
        _running = true;
        _thread = new Thread(Loop) { IsBackground = true, Name = "YInput-XInput" };
        _thread.Start();
    }

    public void Dispose()
    {
        _running = false;
        _thread?.Join(1000);
    }

    private void Loop()
    {
        while (_running)
        {
            bool any = false;
            for (int i = 0; i < 4; i++)
            {
                // 미연결 슬롯은 매번 조회하지 않고 가끔만(비용 절감)
                if (!_connected[i] && _skip[i]-- > 0) continue;
                _skip[i] = 0;

                if (XInputGetState((uint)i, out var state) == 0)
                {
                    any = true;
                    var cur = FromNative(state.Gamepad);
                    if (_connected[i])
                        foreach (var ev in Diff(_prev[i], cur)) Changed?.Invoke(this, ev);
                    _prev[i] = cur;
                    _connected[i] = true;
                }
                else
                {
                    _connected[i] = false;
                    _skip[i] = 120; // ~1초(8ms*120) 후 재확인
                }
            }
            AnyConnected = any;
            Thread.Sleep(8);
        }
    }

    /// <summary>이전/현재 상태 차이를 GamepadEvent 목록으로. (순수 함수 — 테스트 대상)</summary>
    public static IReadOnlyList<GamepadEvent> Diff(in XGamepad prev, in XGamepad cur)
    {
        var list = new List<GamepadEvent>();
        foreach (var (bit, control) in ButtonMap)
        {
            bool p = (prev.Buttons & bit) != 0, c = (cur.Buttons & bit) != 0;
            if (p != c) list.Add(new GamepadEvent { Control = control, Value = c ? 1 : 0 });
        }
        AddTrigger(list, prev.LeftTrigger, cur.LeftTrigger, GamepadControl.LeftTrigger);
        AddTrigger(list, prev.RightTrigger, cur.RightTrigger, GamepadControl.RightTrigger);
        AddStick(list, prev.LX, cur.LX, GamepadControl.LeftStickX);
        AddStick(list, prev.LY, cur.LY, GamepadControl.LeftStickY);
        AddStick(list, prev.RX, cur.RX, GamepadControl.RightStickX);
        AddStick(list, prev.RY, cur.RY, GamepadControl.RightStickY);
        return list;
    }

    private static void AddTrigger(List<GamepadEvent> list, byte p, byte c, GamepadControl control)
    {
        if (p / TriggerStep != c / TriggerStep)
            list.Add(new GamepadEvent { Control = control, Value = c });
    }

    private static int Quantize(short v)
    {
        int d = Math.Abs((int)v) < StickDeadzone ? 0 : v;
        return d / StickStep * StickStep;
    }

    private static void AddStick(List<GamepadEvent> list, short p, short c, GamepadControl control)
    {
        int qp = Quantize(p), qc = Quantize(c);
        if (qp != qc) list.Add(new GamepadEvent { Control = control, Value = qc });
    }

    private static XGamepad FromNative(in XINPUT_GAMEPAD g) => new()
    {
        Buttons = g.wButtons,
        LeftTrigger = g.bLeftTrigger,
        RightTrigger = g.bRightTrigger,
        LX = g.sThumbLX, LY = g.sThumbLY, RX = g.sThumbRX, RY = g.sThumbRY,
    };

    // ---- XInput interop ----
    [StructLayout(LayoutKind.Sequential)]
    private struct XINPUT_GAMEPAD
    {
        public ushort wButtons;
        public byte bLeftTrigger;
        public byte bRightTrigger;
        public short sThumbLX, sThumbLY, sThumbRX, sThumbRY;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XINPUT_STATE
    {
        public uint dwPacketNumber;
        public XINPUT_GAMEPAD Gamepad;
    }

    [DllImport("xinput1_4.dll")]
    private static extern uint XInputGetState(uint dwUserIndex, out XINPUT_STATE pState);
}
