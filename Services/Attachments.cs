using System.IO;

namespace TodoApp.Services;

/// <summary>待办附件（图片）统一存放在 %APPDATA%\TodoApp\attachments\{todoId}\，随待办删除清理</summary>
public static class Attachments
{
    static string Root =>
        Path.Combine(AppPaths.DataDir, "attachments");

    /// <summary>把源图片复制进待办的附件目录，返回存储文件名</summary>
    public static string? AddCopy(Guid todoId, string sourcePath)
    {
        try
        {
            var dir = Path.Combine(Root, todoId.ToString("N"));
            Directory.CreateDirectory(dir);
            var name = Guid.NewGuid().ToString("N")[..8] + Path.GetExtension(sourcePath).ToLowerInvariant();
            File.Copy(sourcePath, Path.Combine(dir, name), true);
            return name;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>把内存中的图片字节（如剪贴板 PNG）写入附件目录</summary>
    public static string? AddCopyBytes(Guid todoId, byte[] data, string ext)
    {
        try
        {
            var dir = Path.Combine(Root, todoId.ToString("N"));
            Directory.CreateDirectory(dir);
            var name = Guid.NewGuid().ToString("N")[..8] + ext;
            File.WriteAllBytes(Path.Combine(dir, name), data);
            return name;
        }
        catch
        {
            return null;
        }
    }

    public static string? GetPath(Guid todoId, string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName) || fileName.Contains("..")) return null;
        try
        {
            var dir = Path.Combine(Root, todoId.ToString("N"));
            var p = Path.GetFullPath(Path.Combine(dir, fileName));
            // 防绝对路径/符号穿越：解析后必须仍位于该待办的附件目录内
            var dirFull = Path.GetFullPath(dir);
            if (!p.StartsWith(dirFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return null;
            return File.Exists(p) ? p : null;
        }
        catch
        {
            return null;
        }
    }

    public static void DeleteAll(Guid todoId)
    {
        try
        {
            var dir = Path.Combine(Root, todoId.ToString("N"));
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
        catch
        {
            // 清理失败不影响删除待办本身
        }
    }

    /// <summary>启动时清理不属于任何任务的孤儿附件目录（测试/草稿残留）</summary>
    public static void CleanupOrphans(IEnumerable<Models.TodoItem> roots)
    {
        try
        {
            if (!Directory.Exists(Root)) return;
            static IEnumerable<Models.TodoItem> Walk(IEnumerable<Models.TodoItem> items)
            {
                foreach (var i in items)
                {
                    yield return i;
                    foreach (var c in Walk(i.Children)) yield return c;
                }
            }
            var keep = Walk(roots).Select(i => i.Id.ToString("N")).ToHashSet();
            foreach (var d in Directory.GetDirectories(Root))
                if (!keep.Contains(Path.GetFileName(d)))
                    Directory.Delete(d, true);
        }
        catch
        {
            // 清理尽力而为
        }
    }
}
