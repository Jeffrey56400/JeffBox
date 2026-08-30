using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace TodoApp.Services;

public class LaunchItem
{
    public string Id { get; set; } = System.Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    /// <summary>累计启动次数（用于"最常用"排序）</summary>
    public int LaunchCount { get; set; }
}

public class LaunchCategory
{
    public string Id { get; set; } = System.Guid.NewGuid().ToString("N");
    /// <summary>空串表示未命名（界面显示为本地化的默认名）</summary>
    public string Name { get; set; } = "";
    public List<LaunchItem> Items { get; set; } = new();
}

public class LauncherData
{
    public List<LaunchCategory> Categories { get; set; } = new();
    /// <summary>记住上次停留的分类</summary>
    public string? SelectedCategory { get; set; }
    /// <summary>true = 按启动次数排序；false = 手动拖动排序</summary>
    public bool SortByCount { get; set; }
}

/// <summary>快捷启动持久化：%APPDATA%\TodoApp\tools.json（多分类结构，兼容旧版平铺数组）</summary>
public static class LauncherStorage
{
    static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    static string FilePath =>
        System.IO.Path.Combine(AppPaths.DataDir, "tools.json");

    public static LauncherData Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return NewData();
            var text = File.ReadAllText(FilePath).TrimStart();
            // 旧版格式：平铺数组 [ {Name,Path}, ... ] → 迁移进默认分类
            if (text.StartsWith("["))
            {
                var legacy = JsonSerializer.Deserialize<List<LaunchItem>>(File.ReadAllText(FilePath))
                             ?? new List<LaunchItem>();
                return new LauncherData
                {
                    Categories = { new LaunchCategory { Name = "", Items = legacy } },
                };
            }
            var data = JsonSerializer.Deserialize<LauncherData>(File.ReadAllText(FilePath));
            if (data == null || data.Categories.Count == 0) return NewData();
            data.Categories.RemoveAll(c => c == null);
            if (data.Categories.Count == 0) return NewData();
            data.Categories.ForEach(c =>
            {
                c.Items ??= new List<LaunchItem>();
                c.Items.RemoveAll(i => i == null);
            });
            return data;
        }
        catch
        {
            return NewData();
        }
    }

    static LauncherData NewData() =>
        new() { Categories = { new LaunchCategory { Name = "" } } };

    public static void Save(LauncherData data)
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(FilePath)!);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(data, JsonOpts));
            if (File.Exists(FilePath)) File.Replace(tmp, FilePath, null);
            else File.Move(tmp, FilePath);
        }
        catch
        {
            // 磁盘异常不崩溃
        }
    }
}
