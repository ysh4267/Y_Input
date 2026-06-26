using YInput.Core.Models;
using YInput.Core.Persistence;

namespace YInput.Core.Tests;

public class MacroSerializationTests
{
    private static Macro SampleMacro() => new()
    {
        Id = "sample01",
        Name = "테스트 매크로",
        LoopCount = 3,
        SpeedMultiplier = 1.5,
        Trigger = new Hotkey { Ctrl = true, Alt = true, VirtualKey = 0x77 }, // Ctrl+Alt+F8
        Steps =
        {
            new MacroStep(new KeyboardEvent { Code = 30, State = 0x00 }, 0),     // A down
            new MacroStep(new KeyboardEvent { Code = 30, State = 0x01 }, 50),    // A up
            new MacroStep(new MouseEvent { Flags = 0x00, X = 10, Y = -5 }, 16),  // relative move
            new MacroStep(new MouseEvent { ButtonState = 0x01 }, 16),           // left down
            new MacroStep(new GamepadEvent { Control = GamepadControl.A, Value = 1 }, 100),
            new MacroStep(new GamepadEvent { Control = GamepadControl.LeftStickX, Value = -20000 }, 16),
            new MacroStep(new GamepadEvent { Control = GamepadControl.RightTrigger, Value = 255 }, 16),
            new MacroStep(new TextEvent { Text = "hello", PerKeyDelayMs = 5 }, 200),
        },
    };

    [Fact]
    public void RoundTrip_PreservesAllFieldsAndPolymorphicTypes()
    {
        var original = SampleMacro();

        var json = MacroStore.Serialize(original);
        var restored = MacroStore.Deserialize(json);

        Assert.Equal(original.Id, restored.Id);
        Assert.Equal(original.Name, restored.Name);
        Assert.Equal(original.LoopCount, restored.LoopCount);
        Assert.Equal(original.SpeedMultiplier, restored.SpeedMultiplier);
        Assert.Equal(original.Steps.Count, restored.Steps.Count);

        Assert.NotNull(restored.Trigger);
        Assert.True(restored.Trigger!.Ctrl);
        Assert.True(restored.Trigger.Alt);
        Assert.Equal(0x77u, restored.Trigger.VirtualKey);

        // 폴리모픽 타입이 보존되는지 확인
        Assert.IsType<KeyboardEvent>(restored.Steps[0].Event);
        Assert.IsType<MouseEvent>(restored.Steps[2].Event);
        Assert.IsType<GamepadEvent>(restored.Steps[4].Event);
        Assert.IsType<TextEvent>(restored.Steps[7].Event);

        var key = (KeyboardEvent)restored.Steps[0].Event;
        Assert.Equal(30, key.Code);
        Assert.False(key.IsKeyUp);

        var pad = (GamepadEvent)restored.Steps[5].Event;
        Assert.Equal(GamepadControl.LeftStickX, pad.Control);
        Assert.Equal(-20000, pad.Value);

        var text = (TextEvent)restored.Steps[7].Event;
        Assert.Equal("hello", text.Text);
        Assert.Equal(50.0, restored.Steps[1].DelayBeforeMs);
    }

    [Fact]
    public void EnumsSerializeAsStrings()
    {
        var json = MacroStore.Serialize(SampleMacro());
        Assert.Contains("LeftStickX", json);   // GamepadControl as string
        Assert.Contains("\"$type\"", json);     // type discriminator present
    }
}

public class MacroLibraryTests
{
    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "yinput-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void SaveLoadList_RoundTrips()
    {
        var dir = TempDir();
        try
        {
            var lib = new MacroLibrary(dir);
            var m = new Macro { Id = "abc", Name = "Z-last" };
            var n = new Macro { Id = "def", Name = "A-first" };
            lib.Save(m);
            lib.Save(n);

            Assert.True(lib.Exists("abc"));
            Assert.True(File.Exists(lib.PathFor("abc")));

            var loaded = lib.Load("abc");
            Assert.NotNull(loaded);
            Assert.Equal("Z-last", loaded!.Name);
            Assert.True(loaded.ModifiedUtc > DateTimeOffset.MinValue);

            var all = lib.LoadAll();
            Assert.Equal(2, all.Count);
            Assert.Equal("A-first", all[0].Name); // 이름 정렬
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Load_MissingId_ReturnsNull()
    {
        var dir = TempDir();
        try
        {
            var lib = new MacroLibrary(dir);
            Assert.Null(lib.Load("nope"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
