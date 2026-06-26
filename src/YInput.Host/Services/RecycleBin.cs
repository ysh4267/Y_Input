using Microsoft.VisualBasic.FileIO;

namespace YInput.Host.Services;

/// <summary>전역 규칙(영구 삭제 금지)에 따라 파일을 휴지통으로 보낸다.</summary>
public static class RecycleBin
{
    public static void Delete(string path)
    {
        if (!File.Exists(path)) return;
        FileSystem.DeleteFile(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
    }
}
