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
    /// <summary>우리가 주입한 스트로크 표식('YINP'). 트리거 감지는 이 표식이 없는 외부 입력만 받아
    /// 매크로 자기 출력으로 트리거가 다시 발동(피드백/연쇄)되는 것을 막는다. LL 훅의 dwExtraInfo로 전달됨.</summary>
    public const uint InjectMark = 0x59494E50;

    private KeyboardHook? _kbHook;
    private MouseHook? _mouseHook;
    private IntPtr _sendContext;   // 전송 전용 컨텍스트(캡처 수신 루프와 분리 → Send 데드락 방지)
    private readonly object _sendGate = new(); // 동시 재생: 여러 매크로가 같은 컨텍스트로 전송 시 직렬화
    private volatile bool _capturing;

    // 연결된 키보드/마우스 디바이스 id 목록(GetDeviceList). 후크의 Device는 "실제 입력이 한 번
    // 들어와야" 학습되지만(그 전엔 0 → 전송 실패), 이 목록은 학습 없이도 유효한 디바이스를 주므로
    // 트리거 직후/켠 직후에도 바로 전송할 수 있다.
    private int[] _kbDevices = Array.Empty<int>();
    private int[] _mouseDevices = Array.Empty<int>();

    /// <summary>드라이버가 설치되어 초기화에 성공했는지.</summary>
    public bool Available { get; }

    /// <summary>키보드로 송출 가능한지(학습됐거나 연결된 키보드 디바이스가 있으면).</summary>
    public bool KeyboardReady => Available && (_kbHook is { CanSimulateInput: true } || _kbDevices.Length > 0);

    /// <summary>마우스로 송출 가능한지.</summary>
    public bool MouseReady => Available && (_mouseHook is { CanSimulateInput: true } || _mouseDevices.Length > 0);

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
            // 전송 전용 컨텍스트: 후크의 컨텍스트는 수신 루프가 interception_wait로 점유하므로,
            // 같은 컨텍스트로 보내면 락 경합으로 행이 걸린다. 별도 컨텍스트로 보내면 경합이 없다.
            _sendContext = InputInterceptor.CreateContext();
            RefreshDevices(); // 연결된 키보드/마우스 디바이스 목록 — 학습 없이도 전송 가능하게
            Available = true;
        }
        catch
        {
            Available = false;
        }
    }

    /// <summary>연결된 키보드/마우스 디바이스 id 목록을 갱신한다.</summary>
    private void RefreshDevices()
    {
        try { _kbDevices = InputInterceptor.GetDeviceList(InputInterceptor.IsKeyboard).Select(d => d.Device).Where(d => d > 0).ToArray(); } catch { }
        try { _mouseDevices = InputInterceptor.GetDeviceList(InputInterceptor.IsMouse).Select(d => d.Device).Where(d => d > 0).ToArray(); } catch { }
    }

    /// <summary>키보드 전송 대상 디바이스: 학습됐으면 그 디바이스, 아니면 연결된 첫 키보드.</summary>
    private int KeyboardSendDevice()
    {
        if (_kbHook is { CanSimulateInput: true, Device: > 0 }) return _kbHook.Device;
        if (_kbDevices.Length == 0) RefreshDevices();
        return _kbDevices.Length > 0 ? _kbDevices[0] : (_kbHook?.Device ?? 0);
    }

    private int MouseSendDevice()
    {
        if (_mouseHook is { CanSimulateInput: true, Device: > 0 }) return _mouseHook.Device;
        if (_mouseDevices.Length == 0) RefreshDevices();
        return _mouseDevices.Length > 0 ? _mouseDevices[0] : (_mouseHook?.Device ?? 0);
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
        if (!Available || _kbHook is null)
            throw new InputNotReadyException("Interception 드라이버를 사용할 수 없습니다. 설치 후 재부팅했는지 확인하세요.");
        var stroke = new Stroke
        {
            Key = new KeyStroke { Code = (KeyCode)ke.Code, State = (KeyState)ke.State, Information = InjectMark },
        };
        var ctx = _sendContext != IntPtr.Zero ? _sendContext : _kbHook.Context;
        var dev = KeyboardSendDevice(); // 학습된 디바이스 우선, 없으면 연결된 첫 키보드(학습 불필요)
        lock (_sendGate) InputInterceptor.Send(ctx, dev, ref stroke, 1);
    }

    public void SendMouse(MouseEvent me)
    {
        if (!Available || _mouseHook is null)
            throw new InputNotReadyException("Interception 드라이버를 사용할 수 없습니다. 설치 후 재부팅했는지 확인하세요.");
        var stroke = new Stroke
        {
            Mouse = new MouseStroke
            {
                State = (MouseState)me.ButtonState,
                Flags = (MouseFlags)me.Flags,
                Rolling = me.Rolling,
                X = me.X,
                Y = me.Y,
                Information = InjectMark,
            },
        };
        var ctx = _sendContext != IntPtr.Zero ? _sendContext : _mouseHook.Context;
        var dev = MouseSendDevice();
        lock (_sendGate) InputInterceptor.Send(ctx, dev, ref stroke, 1);
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
        try { if (_sendContext != IntPtr.Zero) { InputInterceptor.DestroyContext(_sendContext); _sendContext = IntPtr.Zero; } } catch { /* ignore */ }
        try { _kbHook?.Dispose(); } catch { /* ignore */ }
        try { _mouseHook?.Dispose(); } catch { /* ignore */ }
        try { InputInterceptor.Dispose(); } catch { /* ignore */ }
    }
}
