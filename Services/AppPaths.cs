using System.IO;

namespace TodoApp.Services;

/// <summary>
/// 数据目录统一入口：%APPDATA%\JeffBox。
/// v1.0 起名 JeffBox，首次启动自动从旧版 TodoApp 目录迁移（原目录保留，可回退旧版）。
/// </summary>
public static class AppPaths
{
    public static string DataDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "JeffBox");

    static string LegacyDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TodoApp");

    /// <summary>确保数据目录存在；首次运行时从旧版目录整体迁移</summary>
    public static void EnsureMigrated()
    {
        try
        {
            if (Directory.Exists(DataDir) || !Directory.Exists(LegacyDir)) return;
            Directory.CreateDirectory(DataDir);
            foreach (var entry in Directory.GetFileSystemEntries(LegacyDir))
            {
                var name = Path.GetFileName(entry);
                var target = Path.Combine(DataDir, name);
                if (Directory.Exists(entry))
                    CopyDir(entry, target);
                else
                    File.Copy(entry, target);
            }
        }
        catch
        {
            // 迁移失败不致命：以空数据启动即可
        }
    }

    static void CopyDir(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (var f in Directory.GetFiles(src))
            File.Copy(f, Path.Combine(dst, Path.GetFileName(f)));
        foreach (var d in Directory.GetDirectories(src))
            CopyDir(d, Path.Combine(dst, Path.GetFileName(d)));
    }
}
