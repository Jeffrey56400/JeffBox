using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using Color = System.Windows.Media.Color;
using Brush = System.Windows.Media.Brush;
using Application = System.Windows.Application;
using ColorConverter = System.Windows.Media.ColorConverter;

namespace TodoApp.Services;

/// <summary>
/// 主题服务：把命名画刷注册到 Application.Resources（不冻结的可变 SolidColorBrush），
/// 切换主题时直接改画刷的 Color，所有 StaticResource 引用即时更新。
/// </summary>
public static class Theme
{
    public const string Light = "light";
    public const string Dark = "dark";

    static readonly Dictionary<string, (Color Light, Color Dark)> Palette = new()
    {
        ["Bg"] = (FromHex("#F4F5F8"), FromHex("#171A22")),
        ["Card"] = (FromHex("#FFFFFF"), FromHex("#222633")),
        ["SegCheckedBg"] = (FromHex("#FFFFFF"), FromHex("#323848")),
        ["Surface2"] = (FromHex("#F3F4FA"), FromHex("#2A2F3C")),
        ["Pill"] = (FromHex("#ECEEF5"), FromHex("#2C3140")),
        ["Line"] = (FromHex("#E6E8F1"), FromHex("#383E4E")),
        ["HoverBg"] = (FromHex("#EAECF4"), FromHex("#2C3140")),
        ["ChipBg"] = (FromHex("#F1F3FA"), FromHex("#2C3140")),
        ["TrackOff"] = (FromHex("#DCE0EC"), FromHex("#3A4050")),
        ["ScrollThumb"] = (FromHex("#D4D8E6"), FromHex("#3E4456")),
        ["TextMain"] = (FromHex("#262B3B"), FromHex("#EEF0F6")),
        ["TextBody"] = (FromHex("#3A3F55"), FromHex("#C9CDDA")),
        ["TextSub"] = (FromHex("#8B90A6"), FromHex("#8F96AB")),
        ["TextFaint"] = (FromHex("#A2A7BC"), FromHex("#6A7186")),
        ["Accent"] = (FromHex("#5B7CFF"), FromHex("#7B95FF")),
        ["AccentHover"] = (FromHex("#4968F2"), FromHex("#6B87F5")),
        ["AccentPress"] = (FromHex("#3D5AE0"), FromHex("#5A78E8")),
        ["Danger"] = (FromHex("#FF5A5F"), FromHex("#FF6B6F")),
        ["DangerBg"] = (FromHex("#FFECEC"), FromHex("#3A2730")),
        ["DueChipBg"] = (FromHex("#F1F2F8"), FromHex("#2C3140")),
        ["DueChipFg"] = (FromHex("#6A7089"), FromHex("#9BA1B5")),
        ["OverdueBg"] = (FromHex("#FFE9EA"), FromHex("#3B2730")),
        ["OverdueFg"] = (FromHex("#E5484D"), FromHex("#FF7B7F")),
        ["SubPillBg"] = (FromHex("#EEF7F1"), FromHex("#24332C")),
        ["SubPillFg"] = (FromHex("#3DA97B"), FromHex("#43C896")),
    };

    public static string Current { get; private set; } = Light;

    /// <summary>在窗口创建前注册全部画刷（浅色初值，未冻结以便运行时改色）</summary>
    public static void EnsureRegistered()
    {
        var res = Application.Current.Resources;
        foreach (var kv in Palette)
        {
            if (!res.Contains(kv.Key))
                res[kv.Key] = new SolidColorBrush(kv.Value.Light); // 保持未冻结
        }
    }

    public static void Apply(string mode)
    {
        Current = mode == Dark ? Dark : Light;
        var res = Application.Current.Resources;
        foreach (var kv in Palette)
        {
            // 画刷在资源字典里会被 WPF 冻结，不能原地改色；
            // XAML 用 DynamicResource 引用，这里直接替换实例即可全局生效
            var target = Current == Dark ? kv.Value.Item2 : kv.Value.Item1;
            var brush = new SolidColorBrush(target);
            brush.Freeze();
            res[kv.Key] = brush;
        }
    }

    /// <summary>给代码层（MarkdownLite 等）取主题画刷，取不到时退回浅色值</summary>
    public static Brush Brush(string key)
    {
        var brush = Application.Current.TryFindResource(key) as SolidColorBrush;
        if (brush != null) return brush;
        var fallback = Palette.TryGetValue(key, out var c) ? c.Item1 : Colors.Gray;
        var b = new SolidColorBrush(fallback);
        b.Freeze();
        return b;
    }

    static Color FromHex(string hex) =>
        (Color)ColorConverter.ConvertFromString(hex);
}
