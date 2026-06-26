using Nefarius.ViGEm.Client;
using Nefarius.ViGEm.Client.Targets;
using Nefarius.ViGEm.Client.Targets.Xbox360;
using YInput.Core.Models;

namespace YInput.Input.ViGEm;

/// <summary>
/// ViGEmBus 기반 가상 Xbox360 게임패드 백엔드. 연결 시 OS에 진짜 컨트롤러로 나타난다.
/// </summary>
public sealed class GamepadBackend : IDisposable
{
    private readonly ViGEmClient? _client;
    private IXbox360Controller? _pad;

    /// <summary>ViGEmBus 드라이버에 연결됐는지(설치 여부).</summary>
    public bool Available { get; }

    /// <summary>가상 패드가 현재 연결(plug-in)돼 있는지.</summary>
    public bool Connected { get; private set; }

    public GamepadBackend()
    {
        try
        {
            _client = new ViGEmClient();
            Available = true;
        }
        catch
        {
            Available = false;
        }
    }

    public void Connect()
    {
        if (!Available || _client is null)
            throw new InputNotReadyException("ViGEmBus 드라이버를 사용할 수 없습니다. 설치되었는지 확인하세요.");
        if (Connected) return;

        _pad = _client.CreateXbox360Controller();
        _pad.Connect();
        Connected = true;
    }

    public void Disconnect()
    {
        if (_pad is not null)
        {
            try { _pad.Disconnect(); } catch { /* ignore */ }
            _pad = null;
        }
        Connected = false;
    }

    public void Send(GamepadEvent e)
    {
        if (_pad is null)
            throw new InputNotReadyException("가상 게임패드가 연결되지 않았습니다. 먼저 패드를 연결하세요.");

        switch (GamepadControls.KindOf(e.Control))
        {
            case GamepadControlKind.Button:
                _pad.SetButtonState(MapButton(e.Control), e.Value != 0);
                break;
            case GamepadControlKind.Axis:
                _pad.SetAxisValue(MapAxis(e.Control), (short)Math.Clamp(e.Value, short.MinValue, short.MaxValue));
                break;
            case GamepadControlKind.Trigger:
                _pad.SetSliderValue(MapSlider(e.Control), (byte)Math.Clamp(e.Value, 0, 255));
                break;
        }
        _pad.SubmitReport();
    }

    private static Xbox360Button MapButton(GamepadControl c) => c switch
    {
        GamepadControl.A => Xbox360Button.A,
        GamepadControl.B => Xbox360Button.B,
        GamepadControl.X => Xbox360Button.X,
        GamepadControl.Y => Xbox360Button.Y,
        GamepadControl.LeftShoulder => Xbox360Button.LeftShoulder,
        GamepadControl.RightShoulder => Xbox360Button.RightShoulder,
        GamepadControl.Back => Xbox360Button.Back,
        GamepadControl.Start => Xbox360Button.Start,
        GamepadControl.Guide => Xbox360Button.Guide,
        GamepadControl.LeftThumb => Xbox360Button.LeftThumb,
        GamepadControl.RightThumb => Xbox360Button.RightThumb,
        GamepadControl.DpadUp => Xbox360Button.Up,
        GamepadControl.DpadDown => Xbox360Button.Down,
        GamepadControl.DpadLeft => Xbox360Button.Left,
        GamepadControl.DpadRight => Xbox360Button.Right,
        _ => throw new ArgumentOutOfRangeException(nameof(c), c, "버튼이 아님"),
    };

    private static Xbox360Axis MapAxis(GamepadControl c) => c switch
    {
        GamepadControl.LeftStickX => Xbox360Axis.LeftThumbX,
        GamepadControl.LeftStickY => Xbox360Axis.LeftThumbY,
        GamepadControl.RightStickX => Xbox360Axis.RightThumbX,
        GamepadControl.RightStickY => Xbox360Axis.RightThumbY,
        _ => throw new ArgumentOutOfRangeException(nameof(c), c, "축이 아님"),
    };

    private static Xbox360Slider MapSlider(GamepadControl c) => c switch
    {
        GamepadControl.LeftTrigger => Xbox360Slider.LeftTrigger,
        GamepadControl.RightTrigger => Xbox360Slider.RightTrigger,
        _ => throw new ArgumentOutOfRangeException(nameof(c), c, "트리거가 아님"),
    };

    public void Dispose()
    {
        Disconnect();
        try { _client?.Dispose(); } catch { /* ignore */ }
    }
}
