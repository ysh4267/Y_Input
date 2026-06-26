using System.Text.Json;
using System.Text.Json.Serialization;
using YInput.Core.Models;

namespace YInput.Core.Persistence;

/// <summary>매크로 ↔ JSON 직렬화. 폴리모픽 InputEvent를 일관된 옵션으로 처리한다.</summary>
public static class MacroStore
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string Serialize(Macro macro) =>
        JsonSerializer.Serialize(macro, Options);

    public static Macro Deserialize(string json) =>
        JsonSerializer.Deserialize<Macro>(json, Options)
        ?? throw new InvalidDataException("매크로 JSON 역직렬화 결과가 null 입니다.");

    public static void Save(Macro macro, string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, Serialize(macro));
    }

    public static Macro Load(string path) => Deserialize(File.ReadAllText(path));
}
