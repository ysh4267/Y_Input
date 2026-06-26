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

    public InputBackend()
    {
        KeyboardMouse = new InterceptionBackend();
        Gamepad = new GamepadBackend();
        KeyboardMouse.Captured += (_, e) => Captured?.Invoke(this, e);
    }

    // ---- 상태 (Host 상태 API에서 노출) ----
    public bool InterceptionAvailable => KeyboardMouse.Available;
    public bool KeyboardReady => KeyboardMouse.KeyboardReady;
    public bool MouseReady => KeyboardMouse.MouseReady;
    public bool GamepadAvailable => Gamepad.Available;
    public bool GamepadConnected => Gamepad.Connected;

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
            default: throw new NotSupportedException($"지원하지 않는 이벤트 타입: {e.GetType().Name}");
        }
    }

    public void Dispose()
    {
        KeyboardMouse.Dispose();
        Gamepad.Dispose();
    }
}
