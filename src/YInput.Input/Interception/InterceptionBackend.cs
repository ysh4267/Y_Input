using InputInterceptorNS;
using YInput.Core.Models;

namespace YInput.Input.Interception;

/// <summary>
/// Interception 드라이버 기반 키보드·마우스 백엔드.
/// 고수준 훅으로 캡처(녹화)·디바이스 학습·텍스트 입력을 처리하고,
/// 녹화된 원시 스트로크는 정적 <see cref="InputInterceptor.Send"/>로 그대로 재생한다.
///
/// 디바이스 제약: Interception은 실제 입력이 한 번 들어와야 device id를 알 수 있다.
/// 그 전에는 <see cref="KeyboardReady"/>/<see cref="MouseReady"/> 가 false이며 송출 시 예외.
/// </summary>
public sealed class InterceptionBackend : IDisposable
{
    private KeyboardHook? _kbHook;
    private MouseHook? _mouseHook;
    private volatile bool _capturing;

    /// <summary>드라이버가 설치되어 초기화에 성공했는지.</summary>
    public bool Available { get; }

    /// <summary>키보드 디바이스가 학습되어 송출 가능한지.</summary>
    public bool KeyboardReady => Available && _kbHook is { CanSimulateInput: true };

    /// <summary>마우스 디바이스가 학습되어 송출 가능한지.</summary>
    public bool MouseReady => Available && _mouseHook is { CanSimulateInput: true };

    public bool IsCapturing => _capturing;

    /// <summary>녹화 중 캡처된 입력 이벤트.</summary>
    public event EventHandler<InputEvent>? Captured;

    public InterceptionBackend()
    {
        try
        {
            if (!InputInterceptor.CheckDriverInstalled()) { Available = false; return; }
            if (!InputInterceptor.Initialize()) { Available = false; return; }

            // 필터 All: 모든 키보드/마우스 입력을 가로채되 콜백에서 변경하지 않아 그대로 통과시킨다.
            // 이 과정에서 device id가 학습되고, 녹화 시 이벤트를 캡처한다.
            _kbHook = new KeyboardHook(KeyboardFilter.All, OnKeyboardStroke);
            _mouseHook = new MouseHook(MouseFilter.All, OnMouseStroke);
            Available = true;
        }
        catch
        {
            Available = false;
        }
    }

    public void StartCapture() => _capturing = true;
    public void StopCapture() => _capturing = false;

    private void OnKeyboardStroke(ref KeyStroke ks)
    {
        if (_capturing)
            Captured?.Invoke(this, new KeyboardEvent { Code = (ushort)ks.Code, State = (ushort)ks.State });
        // stroke 미변경 → 훅이 pass-through
    }

    private void OnMouseStroke(ref MouseStroke ms)
    {
        if (_capturing)
            Captured?.Invoke(this, new MouseEvent
            {
                ButtonState = (ushort)ms.State,
                Flags = (ushort)ms.Flags,
                Rolling = ms.Rolling,
                X = ms.X,
                Y = ms.Y,
            });
    }

    public void SendKeyboard(KeyboardEvent ke)
    {
        EnsureKeyboard();
        var stroke = new Stroke
        {
            Key = new KeyStroke { Code = (KeyCode)ke.Code, State = (KeyState)ke.State },
        };
        InputInterceptor.Send(_kbHook!.Context, _kbHook.Device, ref stroke, 1);
    }

    public void SendMouse(MouseEvent me)
    {
        EnsureMouse();
        var stroke = new Stroke
        {
            Mouse = new MouseStroke
            {
                State = (MouseState)me.ButtonState,
                Flags = (MouseFlags)me.Flags,
                Rolling = me.Rolling,
                X = me.X,
                Y = me.Y,
            },
        };
        InputInterceptor.Send(_mouseHook!.Context, _mouseHook.Device, ref stroke, 1);
    }

    public void SendText(TextEvent te)
    {
        EnsureKeyboard();
        _kbHook!.SimulateInput(te.Text, 0, te.PerKeyDelayMs);
    }

    private void EnsureKeyboard()
    {
        if (!Available || _kbHook is null)
            throw new InputNotReadyException("Interception 드라이버를 사용할 수 없습니다. 설치 후 재부팅했는지 확인하세요.");
        if (!_kbHook.CanSimulateInput)
            throw new InputNotReadyException("키보드 디바이스가 아직 인식되지 않았습니다. 실제 키를 한 번 누른 뒤 다시 시도하세요.");
    }

    private void EnsureMouse()
    {
        if (!Available || _mouseHook is null)
            throw new InputNotReadyException("Interception 드라이버를 사용할 수 없습니다. 설치 후 재부팅했는지 확인하세요.");
        if (!_mouseHook.CanSimulateInput)
            throw new InputNotReadyException("마우스 디바이스가 아직 인식되지 않았습니다. 마우스를 한 번 움직인 뒤 다시 시도하세요.");
    }

    public void Dispose()
    {
        try { _kbHook?.Dispose(); } catch { /* ignore */ }
        try { _mouseHook?.Dispose(); } catch { /* ignore */ }
        try { InputInterceptor.Dispose(); } catch { /* ignore */ }
    }
}
