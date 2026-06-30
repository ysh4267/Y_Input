using YInput.Core.Models;

namespace YInput.Core.Persistence;

/// <summary>
/// 폴더 기반 매크로 보관소. 각 매크로는 <c>{id}.json</c> 파일 하나로 저장된다.
/// 삭제는 파일 경로만 반환하고, 실제 휴지통 이동은 호출측(Windows 의존)에서 수행한다
/// (전역 규칙: 영구 삭제 금지).
/// </summary>
public sealed class MacroLibrary
{
    public string Directory { get; }

    public MacroLibrary(string directory)
    {
        Directory = directory;
        System.IO.Directory.CreateDirectory(directory);
    }

    public string PathFor(string id) => Path.Combine(Directory, id + ".json");

    public IReadOnlyList<Macro> LoadAll()
    {
        var list = new List<Macro>();
        foreach (var file in System.IO.Directory.EnumerateFiles(Directory, "*.json"))
        {
            try { list.Add(MacroStore.Load(file)); }
            catch { /* 손상 파일은 건너뜀 */ }
        }
        return list.OrderBy(m => m.Order).ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public Macro? Load(string id)
    {
        var path = PathFor(id);
        return File.Exists(path) ? MacroStore.Load(path) : null;
    }

    public void Save(Macro macro)
    {
        macro.ModifiedUtc = DateTimeOffset.UtcNow;
        MacroStore.Save(macro, PathFor(macro.Id));
    }

    public bool Exists(string id) => File.Exists(PathFor(id));
}
