using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TodoApp.Services;
using FontFamily = System.Windows.Media.FontFamily;
using Brush = System.Windows.Media.Brush;
using Image = System.Windows.Controls.Image;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using Orientation = System.Windows.Controls.Orientation;

namespace TodoApp;

/// <summary>
/// 轻量 Markdown 渲染：标题 / 加粗 / 斜体 / 行内代码 / 代码块 / 无序列表 / 链接 / ![图片](附件名)。
/// 只渲染这一层小语法，避免引入完整 MD 库的体积和开销。
/// </summary>
public static partial class MarkdownLite
{
    static readonly FontFamily Mono = new("Consolas, Courier New");
    [GeneratedRegex(@"(==[^=\n]+==)|(`[^`\n]+`)|(\*[^*\n]+\*)|(\*[^*\s][^*\n]*\*)|(~~[^~\n]+~~)|(\[[^\]\n]+\]\([^)\n]+\))|(\[^\]\n]+\])|($[^$\n]+$)")]
    private static partial Regex InlineToken();

    [GeneratedRegex(@"^!\[[^\]]*\]\(([^)]+)\)$")]
    private static partial Regex ImageLine();

    [GeneratedRegex(@"^[-*]\s+\[([\ xX])\]\s+(.*)$")]
    private static partial Regex TaskLine();

    [GeneratedRegex(@"^(\d+)\.\s+(.*)$")]
    private static partial Regex OrderedListLine();

    [GeneratedRegex(@"^\|?\s*:?-{3,}:?\s*(\|\s*:?-{3,}:?\s*)+\|?$")]
    private static partial Regex TableDivider();

    [GeneratedRegex(@"^\[\^([^\]]+)\]:\s*(.*)$")]
    private static partial Regex FootnoteDef();

    [GeneratedRegex(@"^(\s*)[-*+]\s+(.+)$")]
    private static partial Regex UnorderedLine();
public static void Render(StackPanel host, string md, Func<string, string?> imageResolver)
    {
        host.Children.Clear();
        try
        {
            foreach (var el in EnumerateBlocks(md, imageResolver))
                host.Children.Add(el);
        }
        catch (Exception ex)
        {
            // 渲染异常绝不冒泡成闪退：显示错误块并保留原文
            App.LogCrash("MarkdownRender", ex);
            var tb = MakeText(Loc.Get("MdRenderFail"), 12.5, FontWeights.Normal, "Danger");
            tb.Margin = new System.Windows.Thickness(0, 0, 0, 8);
            host.Children.Add(tb);
        }
    }

    /// <summary>惰性逐块产出元素：调用方可分块消费，超大文件无需一次性建完整视觉树</summary>
    public static IEnumerable<FrameworkElement> EnumerateBlocks(string md, Func<string, string?> imageResolver)
    {
        if (string.IsNullOrWhiteSpace(md))
        {
            yield return MakeText(Loc.Get("NoDetails"), 12.5, FontWeights.Normal, "TextFaint");
            yield break;
        }

        var codeBuf = new List<string>();
        var inCode = false;
        var tableBuf = new List<string>();
        var inTable = false;
        var ready = new Queue<FrameworkElement>();

        foreach (var raw in md.Replace("\r\n", "\n").Split('\n'))
        {
            var t = raw.Trim();

            if (t.StartsWith("```"))
            {
                if (inTable) { FlushTable(); foreach (var e2 in ready) yield return e2; ready.Clear(); }
                if (inCode) { yield return MakeCode(string.Join("\n", codeBuf)); codeBuf.Clear(); }
                inCode = !inCode;
                continue;
            }
            if (inCode) { codeBuf.Add(raw); continue; }

            if (t.StartsWith("|") && t.EndsWith("|") && t.Length > 2)
            {
                if (!inTable) { inTable = true; tableBuf.Clear(); }
                tableBuf.Add(t);
                continue;
            }
            if (inTable) { FlushTable(); foreach (var e2 in ready) yield return e2; ready.Clear(); }

            if (t.Length == 0)
            {
                yield return new Border { Height = 6 };
                continue;
            }

            if (t is "---" or "***" or "___")
            {
                yield return MakeDivider();
                continue;
            }

            if (t.StartsWith("#### ")) { yield return MakeText(t[5..], 13, FontWeights.SemiBold, "TextMain"); continue; }
            if (t.StartsWith("### ")) { yield return MakeText(t[4..], 13.5, FontWeights.SemiBold, "TextMain"); continue; }
            if (t.StartsWith("## ")) { yield return MakeText(t[3..], 14.5, FontWeights.SemiBold, "TextMain"); continue; }
            if (t.StartsWith("# ")) { yield return MakeText(t[2..], 16.5, FontWeights.SemiBold, "TextMain"); continue; }

            if (t.StartsWith("> ")) { yield return MakeQuote(t[2..]); continue; }
            if (t == ">") { yield return MakeQuote(""); continue; }

            var fn = FootnoteDef().Match(t);
            if (fn.Success)
            {
                yield return MakeFootnote(fn.Groups[1].Value, fn.Groups[2].Value);
                continue;
            }
            if (t.StartsWith("$$") && t.EndsWith("$$") && t.Length >= 4)
            {
                yield return MakeMathBlock(t[2..^2]);
                continue;
            }

            var img = ImageLine().Match(t);
            if (img.Success)
            {
                yield return MakeImage(img.Groups[1].Value, img.Groups[2].Value, imageResolver);
                continue;
            }

            var task = TaskLine().Match(t);
            if (task.Success)
            {
                yield return MakeTask(task.Groups[2].Value.Trim(),
                    task.Groups[1].Value.Equals("x", StringComparison.OrdinalIgnoreCase));
                continue;
            }

            // 无序列表：任意缩进深度
            var ul = UnorderedLine().Match(raw);
            if (ul.Success)
            {
                int depth = 0;
                foreach (var ch in ul.Groups[1].Value) depth += ch == '	' ? 2 : 1;
                depth = Math.Min(depth / 2, 5);
                var glyph = depth % 3 == 0 ? "\u2022" : depth % 3 == 1 ? "\u25E6" : "\u25AA";
                var tb = MakeText(glyph + "  " + ul.Groups[2].Value.Trim(), depth == 0 ? 12.5 : 12,
                    FontWeights.Normal, "TextBody");
                tb.Margin = new Thickness(2 + depth * 16, 1, 0, 1);
                yield return tb;
                continue;
            }

            var ol = OrderedListLine().Match(t);
            if (ol.Success)
            {
                var tb3 = MakeText(ol.Groups[1].Value + ".  " + ol.Groups[2], 12.5, FontWeights.Normal, "TextBody");
                tb3.Margin = new Thickness(2, 1, 0, 1);
                yield return tb3;
                continue;
            }

            yield return MakeText(t, 12.5, FontWeights.Normal, "TextBody");
        }

        if (inCode && codeBuf.Count > 0)
            yield return MakeCode(string.Join("\n", codeBuf));
        if (inTable) { FlushTable(); foreach (var e2 in ready) yield return e2; ready.Clear(); }
        yield break;

        void FlushTable()
        {
            inTable = false;
            if (tableBuf.Count == 0) return;
            var hasHeader = tableBuf.Count >= 2 && TableDivider().IsMatch(tableBuf[1]);
            var rows = tableBuf.ToList();
            if (hasHeader) rows.RemoveAt(1);
            if (rows.Count == 0) { tableBuf.Clear(); return; }

            var cols = 0;
            foreach (var r in rows) cols = Math.Max(cols, r.Split('|').Length - 2);
            if (cols <= 0) { tableBuf.Clear(); return; }

            var grid = new Grid { Margin = new Thickness(0, 4, 0, 4) };
            for (int c = 0; c < cols; c++) grid.ColumnDefinitions.Add(new ColumnDefinition());
            for (int r = 0; r < rows.Count; r++)
            {
                grid.RowDefinitions.Add(new RowDefinition());
                var cells = rows[r].Split('|').Skip(1).Take(cols).Select(c => c.Trim()).ToList();
                for (int c = 0; c < cols; c++)
                {
                    var isHead = hasHeader && r == 0;
                    var cell = new Border
                    {
                        BorderBrush = Theme.Brush("Line"),
                        BorderThickness = new Thickness(0.5),
                        Padding = new Thickness(8, 5, 8, 5),
                        Background = isHead ? Theme.Brush("Pill") : null,
                        Child = MakeText(c < cells.Count ? cells[c] : "", 12,
                            isHead ? FontWeights.SemiBold : FontWeights.Normal,
                            isHead ? "TextMain" : "TextBody"),
                    };
                    Grid.SetRow(cell, r);
                    Grid.SetColumn(cell, c);
                    grid.Children.Add(cell);
                }
            }
            ready.Enqueue(grid);
            tableBuf.Clear();
        }
    }

    static FrameworkElement MakeFootnote(string label, string text)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(12, 1, 0, 1) };
        var fnLabel = new System.Windows.Controls.TextBlock
        {
            Text = label,
            FontSize = 9.5,
            Foreground = Theme.Brush("Accent"),
            Margin = new Thickness(0, -4, 4, 0),
            VerticalAlignment = VerticalAlignment.Top,
        };
        sp.Children.Add(fnLabel);
        sp.Children.Add(MakeText(text, 11.5, FontWeights.Normal, "TextSub"));
        return sp;
    }

    static FrameworkElement MakeMathBlock(string formula)
    {
        var tb = new System.Windows.Controls.TextBlock
        {
            Text = formula.Trim(),
            FontFamily = new FontFamily("Cambria Math"),
            FontStyle = FontStyles.Italic,
            FontSize = 14,
            Foreground = Theme.Brush("TextBody"),
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
        };
        return new Border
        {
            Background = Theme.Brush("Surface2"),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14, 8, 14, 8),
            Margin = new Thickness(0, 4, 0, 4),
            Child = tb,
        };
    }

    static FrameworkElement MakeDivider() => new Border
    {
        Height = 1,
        Background = Theme.Brush("Line"),
        Margin = new Thickness(0, 8, 0, 8),
    };

    static FrameworkElement MakeQuote(string text)
    {
        var tb = MakeText(text.Length == 0 ? " " : text, 12.5, FontWeights.Normal, "TextSub");
        return new Border
        {
            BorderThickness = new Thickness(3, 0, 0, 0),
            BorderBrush = Theme.Brush("Line"),
            Background = Theme.Brush("Surface2"),
            CornerRadius = new CornerRadius(0, 6, 6, 0),
            Padding = new Thickness(10, 6, 10, 6),
            Margin = new Thickness(0, 3, 0, 3),
            Child = tb,
        };
    }

    static FrameworkElement MakeTask(string text, bool done)
    {
        var box = new Border
        {
            Width = 14,
            Height = 14,
            CornerRadius = new CornerRadius(4),
            BorderThickness = new Thickness(1.4),
            BorderBrush = done ? Theme.Brush("Accent") : Theme.Brush("TrackOff"),
            Background = done ? Theme.Brush("Accent") : null,
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        if (done)
        {
            var check = new System.Windows.Controls.TextBlock
            {
                Text = "\u2713",
                FontSize = 10,
                Foreground = System.Windows.Media.Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            box.Child = check;
        }
        var label = MakeText(text, 12.5, FontWeights.Normal, done ? "TextFaint" : "TextBody");
        if (done)
            label.TextDecorations = TextDecorations.Strikethrough;
        var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(2, 2, 0, 2) };
        sp.Children.Add(box);
        sp.Children.Add(label);
        return sp;
    }

    static TextBlock MakeText(string text, double size, FontWeight weight, string brushKey)
    {
        var tb = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontSize = size,
            FontWeight = weight,
            Foreground = Theme.Brush(brushKey),
            Margin = new Thickness(0, 2, 0, 2),
        };
        FillInlines(tb, text);
        return tb;
    }

    static Border MakeCode(string code) => new()
    {
        Background = Theme.Brush("Surface2"),
        CornerRadius = new CornerRadius(8),
        Padding = new Thickness(10, 7, 10, 7),
        Margin = new Thickness(0, 3, 0, 3),
        Child = new TextBlock
        {
            Text = code,
            FontFamily = Mono,
            FontSize = 12,
            Foreground = Theme.Brush("TextBody"),
            TextWrapping = TextWrapping.Wrap,
        },
    };

    static FrameworkElement MakeImage(string alt, string file, Func<string, string?> resolver)
    {
        var path = resolver(file);
        if (path == null)
        {
            return new TextBlock
            {
                Text = $"🖼 {alt} · {Loc.Get("ImageMissing")}",
                FontSize = 11.5,
                Foreground = Theme.Brush("TextFaint"),
                Margin = new Thickness(0, 3, 0, 3),
            };
        }

        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;   // 读完即释放文件句柄
            bmp.DecodePixelWidth = 520;                   // 限制解码尺寸，控制内存
            bmp.UriSource = new Uri(path);
            bmp.EndInit();
            bmp.Freeze();

            return new Border
            {
                // 白色衬底：透明背景的图片在深色界面上也能看清
                Background = System.Windows.Media.Brushes.White,
                CornerRadius = new CornerRadius(8),
                Margin = new Thickness(0, 4, 0, 4),
                Child = new Image
                {
                    Source = bmp,
                    MaxHeight = 260,
                    Stretch = Stretch.Uniform,
                    HorizontalAlignment = HorizontalAlignment.Left,
                },
            };
        }
        catch
        {
            // 损坏文件或系统不支持的格式（如部分 webp）：按缺失处理，不让预览崩溃
            return new TextBlock
            {
                Text = $"🖼 {alt} · {Loc.Get("ImageMissing")}",
                FontSize = 11.5,
                Foreground = Theme.Brush("TextFaint"),
                Margin = new Thickness(0, 3, 0, 3),
            };
        }
    }

    static void FillInlines(TextBlock tb, string text)
    {
        var pos = 0;
        foreach (Match m in InlineToken().Matches(text))
        {
            if (m.Index > pos)
                tb.Inlines.Add(new Run(text[pos..m.Index]));
            var tok = m.Value;
            if (tok.StartsWith('`'))
            {
                tb.Inlines.Add(new Run(tok[1..^1]) { FontFamily = Mono, Background = Theme.Brush("Surface2") });
            }
            else if (tok.StartsWith("**"))
            {
                tb.Inlines.Add(new Bold(new Run(tok[2..^2])));
            }
            else if (tok.StartsWith('*'))
            {
                tb.Inlines.Add(new Italic(new Run(tok[1..^1])));
            }
            else
            {
                var inner = Regex.Match(tok, @"^\[([^\]]+)\]\(([^)]+)\)$");
                Inline? linkInline = null;
                try
                {
                    var uri = new Uri(inner.Groups[2].Value, UriKind.RelativeOrAbsolute);
                    // 只允许 http/https，防 file:/javascript: 等协议被拉起
                    if (!uri.IsAbsoluteUri ||
                        uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
                    {
                        var link = new Hyperlink(new Run(inner.Groups[1].Value)) { NavigateUri = uri };
                        link.RequestNavigate += (_, e) =>
                        {
                            try { Process.Start(new ProcessStartInfo(e.Uri.ToString()) { UseShellExecute = true }); }
                            catch { /* 链接打不开就忽略 */ }
                        };
                        linkInline = link;
                    }
                }
                catch (UriFormatException) { }

                if (linkInline != null)
                    tb.Inlines.Add(linkInline);
                else
                    tb.Inlines.Add(new Run(tok)); // 非法链接降级为普通文本
            }
            pos = m.Index + tok.Length;
        }
        if (pos < text.Length)
            tb.Inlines.Add(new Run(text[pos..]));
    }
}
