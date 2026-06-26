using YInput.Core.Models;
using YInput.Input.Interception;
using YInput.Input.ViGEm;

namespace YInput.Input;

/// <summary>
/// 키보드·마우스(Interception)와 게임패드(ViGEm) 백엔드를 묶은 파사드.
/// Engine의 Player는 <see cref="IInputSink"/>, Recorder는 <see cref="IInputSource"/>로 사용한다.
/// </summary>
public sealed class InputBackend : IInputSink, IInputSource, IDisposable
{
    public InterceptionBackend KeyboardMouse { get; }
    public GamepadBackend Gamepad { get; }

    private readonly XInputPoller _xinput = new();

    /// <summary>물리 게임패드(XInput) 입력 변화 — 항상 발생(녹화 여부 무관). 트리거·입력감지용.</summary>
    public event EventHandler<GamepadEvent>? GamepadInput;

    public InputBackend()
    {
        KeyboardMouse = new InterceptionBackend();
        Gamepad = new GamepadBackend();
        KeyboardMouse.Captured += (_, e) => Captured?.Invoke(this, e);
        _xinput.Changed += OnGamepadInput;
        _xinput.Start();
    }

    private void OnGamepadInput(object? sender, GamepadEvent e)
    {
        GamepadInput?.Invoke(this, e);
        if (KeyboardMouse.IsCapturing) Captured?.Invoke(this, e); // 녹화 중이면 캡처 파이프라인으로
    }

    // ---- 상태 (Host 상태 API에서 노출) ----
    public bool InterceptionAvailable => KeyboardMouse.Available;
    public bool KeyboardReady => KeyboardMouse.KeyboardReady;
    public bool MouseReady => KeyboardMouse.MouseReady;
    public bool GamepadAvailable => Gamepad.Available;
    public bool GamepadConnected => Gamepad.Connected;

    /// <summary>물리 게임패드가 하나 이상 연결돼 있는지(XInput).</summary>
    public bool GamepadDevicePresent => _xinput.AnyConnected;

    // ---- IInputSource (녹화) ----
    public event EventHandler<InputEvent>? Captured;
    public bool IsCapturing => KeyboardMouse.IsCapturing;
    public void StartCapture() => KeyboardMouse.StartCapture();
    public void StopCapture() => KeyboardMouse.StopCapture();

    // ---- 게임패드 연결 제어 ----
    public void ConnectGamepad() => Gamepad.Connect();
    public void DisconnectGamepad() => Gamepad.Disconnect();

    // ---- IInputSink (재생) ----
    public void Send(InputEvent e)
    {
        switch (e)
        {
            case KeyboardEvent ke: KeyboardMouse.SendKeyboard(ke); break;
            case MouseEvent me: KeyboardMouse.SendMouse(me); break;
            case TextEvent te: KeyboardMouse.SendText(te); break;
            case GamepadEvent ge: Gamepad.Send(ge); break;
            case DelayEvent: break; // no-op — 대기는 MacroStep.DelayBeforeMs가 담당
            case LoopStartEvent: break; // no-op — 반복 제어는 Player가 담당
            case LoopEndEvent: break;   // no-op — 반복 제어는 Player가 담당
            default: throw new NotSupportedException($"지원하지 않는 이벤트 타입: {e.GetType().Name}");
        }
    }

    public void Dispose()
    {
        _xinput.Dispose();
        KeyboardMouse.Dispose();
        Gamepad.Dispose();
    }
}
