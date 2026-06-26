using YInput.Core.Models;
using YInput.Engine;
using YInput.Input;

namespace YInput.Core.Tests;

/// <summary>드라이버 없이 동작을 검증하기 위한 가짜 송출 대상.</summary>
internal sealed class FakeSink : IInputSink
{
    public List<InputEvent> Sent { get; } = new();
    public void Send(InputEvent e) => Sent.Add(e);
}

public class PlayerTimingTests
{
    [Theory]
    [InlineData(100, 1.0, 100)]
    [InlineData(100, 2.0, 50)]
    [InlineData(100, 0.5, 200)]
    [InlineData(0, 2.0, 0)]
    [InlineData(100, 0, 100)]   // speed<=0 이면 1.0으로 처리
    [InlineData(100, -3, 100)]
    public void EffectiveDelay_AppliesSpeed(double delay, double speed, double expected)
    {
        Assert.Equal(expected, Player.EffectiveDelayMs(delay, speed), 3);
    }
}

public class PlayerPlaybackTests
{
    private static Macro TwoKeyMacro(int loops) => new()
    {
        Name = "t",
        LoopCount = loops,
        SpeedMultiplier = 1.0,
        Steps =
        {
            new MacroStep(new KeyboardEvent { Code = 30, State = 0 }, 0),
            new MacroStep(new KeyboardEvent { Code = 30, State = 1 }, 0),
        },
    };

    [Fact]
    public async Task PlaysAllStepsInOrder()
    {
        var sink = new FakeSink();
        var player = new Player(sink);

        await player.PlayAsync(TwoKeyMacro(loops: 1));

        Assert.Equal(2, sink.Sent.Count);
        Assert.False(((KeyboardEvent)sink.Sent[0]).IsKeyUp);
        Assert.True(((KeyboardEvent)sink.Sent[1]).IsKeyUp);
        Assert.False(player.IsPlaying);
    }

    [Fact]
    public async Task RepeatsByLoopCount()
    {
        var sink = new FakeSink();
        var player = new Player(sink);

        await player.PlayAsync(TwoKeyMacro(loops: 3));

        Assert.Equal(6, sink.Sent.Count);
    }

    [Fact]
    public async Task RaisesStartedAndStopped()
    {
        var sink = new FakeSink();
        var player = new Player(sink);
        bool started = false, stopped = false;
        player.Started += (_, _) => started = true;
        player.Stopped += (_, _) => stopped = true;

        await player.PlayAsync(TwoKeyMacro(loops: 1));

        Assert.True(started);
        Assert.True(stopped);
    }
}

public class RecorderTests
{
    /// <summary>캡처 이벤트를 직접 흘려보낼 수 있는 가짜 소스.</summary>
    private sealed class FakeSource : IInputSource
    {
        public event EventHandler<InputEvent>? Captured;
        public bool IsCapturing { get; private set; }
        public void StartCapture() => IsCapturing = true;
        public void StopCapture() => IsCapturing = false;
        public void Emit(InputEvent e) => Captured?.Invoke(this, e);
    }

    [Fact]
    public void RecordsEventsWithFirstDelayZero()
    {
        var source = new FakeSource();
        var rec = new Recorder(source);

        rec.Start();
        Assert.True(source.IsCapturing);
        source.Emit(new KeyboardEvent { Code = 30, State = 0 });
        source.Emit(new KeyboardEvent { Code = 30, State = 1 });
        var macro = rec.Stop("captured");

        Assert.False(source.IsCapturing);
        Assert.Equal("captured", macro.Name);
        Assert.Equal(2, macro.Steps.Count);
        Assert.Equal(0, macro.Steps[0].DelayBeforeMs); // 첫 스텝 지연 0
        Assert.True(macro.Steps[1].DelayBeforeMs >= 0);
    }
}
