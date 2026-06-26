using YInput.Core.Models;

namespace YInput.Input;

/// <summary>입력 신호를 실제로 발생시키는 대상(드라이버 백엔드).</summary>
public interface IInputSink
{
    /// <summary>이벤트를 실제 입력으로 송출한다. 준비 안 됐으면 <see cref="InputNotReadyException"/>.</summary>
    void Send(InputEvent e);
}

/// <summary>물리 입력을 캡처해 이벤트로 흘려보내는 소스(녹화용).</summary>
public interface IInputSource
{
    event EventHandler<InputEvent>? Captured;
    bool IsCapturing { get; }
    void StartCapture();
    void StopCapture();
}

/// <summary>송출 대상이 아직 준비되지 않았을 때(예: 키보드 디바이스 미학습, 드라이버 미설치).</summary>
public sealed class InputNotReadyException : Exception
{
    public InputNotReadyException(string message) : base(message) { }
}
