using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using TodoApp.Services;
using TextBox = System.Windows.Controls.TextBox;
using Orientation = System.Windows.Controls.Orientation;
using Button = System.Windows.Controls.Button;

namespace TodoApp.Views;

/// <summary>轻量文本输入对话框（跟随主题色），用于分类新建/重命名等</summary>
public class InputDialog : Window
{
    readonly TextBox _box;

    public string InputValue => _box.Text.Trim();

    /// <param name="title">窗口标题</param>
    /// <param name="prompt">输入框上方提示</param>
    /// <param name="initial">初始文本（全选方便覆盖）</param>
    public InputDialog(Window owner, string title, string prompt, string initial = "")
    {
        Owner = owner;
        Title = title;
        Width = 380;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Background = Theme.Brush("Bg");

        var okBtn = new Button
        {
            Style = (Style)FindResource("AddBtn"),
            Content = Loc.Get("Save"),
            MinWidth = 84,
        };
        okBtn.Click += (_, _) => { DialogResult = true; Close(); };

        var cancelBtn = new Button
        {
            Style = (Style)FindResource("GhostBtn"),
            Content = Loc.Get("Cancel"),
        };
        cancelBtn.Click += (_, _) => { DialogResult = false; Close(); };

        _box = new TextBox
        {
            Text = initial,
            FontSize = 13,
            Padding = new Thickness(10, 8, 10, 8),
            Background = Theme.Brush("Surface2"),
            Foreground = Theme.Brush("TextBody"),
            BorderThickness = new Thickness(0),
            CaretBrush = Theme.Brush("Accent"),
            VerticalContentAlignment = VerticalAlignment.Center,
        };

        var root = new Border
        {
            Background = Theme.Brush("Bg"),
            Padding = new Thickness(18, 16, 18, 16),
            Child = new StackPanel
            {
                Children =
                {
                    new TextBlock
                    {
                        Text = prompt,
                        FontSize = 12,
                        Foreground = Theme.Brush("TextSub"),
                        Margin = new Thickness(1, 0, 0, 8),
                    },
                    _box,
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
                        Margin = new Thickness(0, 14, 0, 0),
                        Children = { cancelBtn, BuildGap(), okBtn },
                    },
                },
            },
        };

        Content = root;
        Loaded += (_, _) =>
        {
            _box.Focus();
            _box.SelectAll();
        };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { DialogResult = false; Close(); }
        };
        _box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { e.Handled = true; DialogResult = true; Close(); }
        };
    }

    static FrameworkElement BuildGap() => new Border { Width = 10 };
}
