using System.IO;
using System.IO.Compression;

namespace TodoApp.Services;

/// <summary>
/// 全量数据备份：把 %APPDATA%\JeffBox 打包成 zip（todos / tools / settings / attachments），
/// 导入时整目录覆盖并以重启生效。zip 用系统内置 System.IO.Compression，零外部依赖。
/// </summary>
public static class DataBackup
{
    /// <summary>把数据目录打包到 zipPath（已存在则覆盖）</summary>
    public static void Export(string zipPath)
    {
        var dir = AppPaths.DataDir;
        if (File.Exists(zipPath)) File.Delete(zipPath);
        ZipFile.CreateFromDirectory(dir, zipPath, CompressionLevel.Optimal, includeBaseDirectory: false);
    }

    /// <summary>导出到临时目录（导入前对当前数据的安全副本）</summary>
    public static string ExportToTemp()
    {
        var path = Path.Combine(Path.GetTempPath(), $"JeffBox-preimport-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
        Export(path);
        return path;
    }

    /// <summary>是否像本应用导出的备份（根层有 todos.json 或 tools.json）</summary>
    public static bool LooksLikeBackup(string zipPath)
    {
        try
        {
            using var zip = ZipFile.OpenRead(zipPath);
            return zip.Entries.Any(e => e.FullName is "todos.json" or "tools.json");
        }
        catch
        {
            return false;
        }
    }

    /// <summary>解压整包覆盖数据目录（导入即回滚到备份时刻；随后由调用方重启应用）</summary>
    public static void Restore(string zipPath)
    {
        var dir = AppPaths.DataDir;
        Directory.CreateDirectory(dir);
        ZipFile.ExtractToDirectory(zipPath, dir, overwriteFiles: true);
    }
}
