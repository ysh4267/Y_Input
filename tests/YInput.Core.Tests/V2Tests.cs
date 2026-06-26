using YInput.Core.Models;
using YInput.Core.Persistence;
using YInput.Engine;
using YInput.Input;

namespace YInput.Core.Tests;

public class DelayEventTests
{
    [Fact]
    public void DelayEvent_RoundTrips()
    {
        var macro = new Macro
        {
            Name = "wait-test",
            RandomizeDelayPercent = 25,
            Steps = { new MacroStep(new DelayEvent(), 250) },
        };

        var restored = MacroStore.Deserialize(MacroStore.Serialize(macro));

        Assert.Equal(25, restored.RandomizeDelayPercent);
        Assert.Single(restored.Steps);
        Assert.IsType<DelayEvent>(restored.Steps[0].Event);
        Assert.Equal(250, restored.Steps[0].DelayBeforeMs);
    }
}

public class MouseTriggerTests
{
    [Fact]
    public void MouseHotkey_RoundTripsAndFormats()
    {
        var macro = new Macro { Name = "m", Trigger = new Hotkey { Mouse = MouseTriggerButton.X1 } };

        var r = MacroStore.Deserialize(MacroStore.Serialize(macro));

        Assert.NotNull(r.Trigger);
        Assert.True(r.Trigger!.IsMouse);
        Assert.False(r.Trigger.IsEmpty);
        Assert.Equal(MouseTriggerButton.X1, r.Trigger.Mouse);
        Assert.Contains("X1", r.Trigger.ToString());
    }

    [Fact]
    public void KeyboardHotkey_NotMouse()
    {
        var hk = new Hotkey { VirtualKey = 0x77 };
        Assert.False(hk.IsMouse);
        Assert.False(hk.IsEmpty);
    }
}

public class MouseClassifyTests
{
    [Fact]
    public void ClassifiesMoveButtonWheel()
    {
        Assert.Equal(MouseEventKind.Move, MouseEvents.Classify(new MouseEvent { X = 5, Y = 0 }));
        Assert.Equal(MouseEventKind.Button, MouseEvents.Classify(new MouseEvent { ButtonState = 0x001 }));
        Assert.Equal(MouseEventKind.Wheel, MouseEvents.Classify(new MouseEvent { ButtonState = 0x400, Rolling = 120 }));
    }
}

public class JitterTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void NoJitter_WhenPercentNonPositive(int pct)
    {
        Assert.Equal(100, Player.ApplyJitter(100, pct, new Random(1)));
    }

    [Fact]
    public void Jitter_StaysWithinBounds()
    {
        var rng = new Random(12345);
        for (int i = 0; i < 1000; i++)
        {
            var v = Player.ApplyJitter(100, 50, rng);
            Assert.InRange(v, 50.0, 150.0);
        }
    }

    [Fact]
    public void Jitter_ActuallyVaries()
    {
        var rng = new Random(7);
        var values = new HashSet<double>();
        for (int i = 0; i < 50; i++) values.Add(Player.ApplyJitter(100, 30, rng));
        Assert.True(values.Count > 5); // 무작위로 흔들림
    }
}

internal sealed class FakeCaptureSource : IInputSource
{
    public event EventHandler<InputEvent>? Captured;
    public bool IsCapturing { get; private set; }
    public void StartCapture() => IsCapturing = true;
    public void StopCapture() => IsCapturing = false;
    public void Emit(InputEvent e) => Captured?.Invoke(this, e);
}

public class RecorderFilterTests
{
    [Fact]
    public void SkipsMouseMove_WhenDisabled()
    {
        var src = new FakeCaptureSource();
        var rec = new Recorder(src);
        rec.Start(new RecordOptions(Keyboard: true, MouseButtons: true, MouseMove: false, MouseWheel: true));

        src.Emit(new MouseEvent { X = 10 });               // move → 제외
        src.Emit(new KeyboardEvent { Code = 30, State = 0 }); // 키 → 기록
        src.Emit(new MouseEvent { ButtonState = 0x001 });    // 좌클릭 → 기록
        src.Emit(new MouseEvent { X = 3 });                  // move → 제외

        var macro = rec.Stop("t");
        Assert.Equal(2, macro.Steps.Count);
        Assert.IsType<KeyboardEvent>(macro.Steps[0].Event);
        Assert.IsType<MouseEvent>(macro.Steps[1].Event);
    }

    [Fact]
    public void AppliesFixedDelay()
    {
        var src = new FakeCaptureSource();
        var rec = new Recorder(src);
        rec.Start(new RecordOptions(FixedDelayMs: 50));

        src.Emit(new KeyboardEvent { Code = 30, State = 0 });
        src.Emit(new KeyboardEvent { Code = 30, State = 1 });
        src.Emit(new KeyboardEvent { Code = 31, State = 0 });

        var macro = rec.Stop("t");
        Assert.Equal(3, macro.Steps.Count);
        Assert.Equal(0, macro.Steps[0].DelayBeforeMs);   // 첫 스텝 0
        Assert.Equal(50, macro.Steps[1].DelayBeforeMs);
        Assert.Equal(50, macro.Steps[2].DelayBeforeMs);
    }
}

public class XInputDiffTests
{
    [Fact]
    public void DetectsButtonPressAndRelease()
    {
        var none = new XGamepad();
        var aDown = new XGamepad { Buttons = 0x1000 }; // A
        Assert.Contains(XInputPoller.Diff(none, aDown), e => e.Control == GamepadControl.A && e.Value == 1);
        Assert.Contains(XInputPoller.Diff(aDown, none), e => e.Control == GamepadControl.A && e.Value == 0);
    }

    [Fact]
    public void IgnoresStickWithinDeadzone()
    {
        var a = new XGamepad { LX = 1000 };
        var b = new XGamepad { LX = 2000 };
        Assert.Empty(XInputPoller.Diff(a, b)); // 둘 다 데드존(8000) 내
    }

    [Fact]
    public void DetectsStickBeyondDeadzone()
    {
        Assert.Contains(XInputPoller.Diff(new XGamepad(), new XGamepad { LX = 20000 }),
            e => e.Control == GamepadControl.LeftStickX);
    }

    [Fact]
    public void DetectsTriggerChange()
    {
        Assert.Contains(XInputPoller.Diff(new XGamepad { LeftTrigger = 0 }, new XGamepad { LeftTrigger = 200 }),
            e => e.Control == GamepadControl.LeftTrigger && e.Value == 200);
    }
}

public class GamepadTriggerTests
{
    [Fact]
    public void GamepadHotkey_RoundTripsAndFormats()
    {
        var m = new Macro { Trigger = new Hotkey { Gamepad = GamepadControl.A } };
        var r = MacroStore.Deserialize(MacroStore.Serialize(m));
        Assert.True(r.Trigger!.IsGamepad);
        Assert.False(r.Trigger.IsMouse);
        Assert.Equal(GamepadControl.A, r.Trigger.Gamepad);
        Assert.Contains("Pad A", r.Trigger.ToString());
    }
}
