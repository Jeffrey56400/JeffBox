using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using TodoApp.Services;
using TextBox = System.Windows.Controls.TextBox;
using Button = System.Windows.Controls.Button;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Point = System.Windows.Point;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Application = System.Windows.Application;
using Orientation = System.Windows.Controls.Orientation;

namespace TodoApp.Views;

/// <summary>
/// 应用内主题化对话框：遮罩 + 圆角卡片 + 缩放淡入，与设置/详情浮层同一套视觉语言，
/// 取代系统 MessageBox 和带系统标题栏的 InputDialog。
/// 挂在主窗口根网格上，Task 返回结果；Esc / 点遮罩 = 取消。
/// </summary>
public static class ThemeDialog
{
    static int _open;

    /// <summary>两态确认。返回 true = 点了确定</summary>
    /// <param name="danger">确定按钮用危险红（删除类操作）</param>
    public static Task<bool> ConfirmAsync(string message, string? okText = null, bool danger = false)
    {
        var (show, result) = Prepare<bool>(out var card, out var buttons, out var actions);

        card.Children.Add(MessageBlock(message));
        var ok = PrimaryButton(okText ?? Loc.Get("Ok"), danger);
        var cancel = new Button { Style = Ghost(), Content = Loc.Get("Cancel") };
        card.Children.Add(ButtonRow(cancel, ok));
        buttons.Add(ok);
        buttons.Add(cancel);
        actions.Add(() => result.TrySetResult(true));
        actions.Add(() => result.TrySetResult(false));

        show(ok);
        return result.Task;
    }

    /// <summary>保存三态确认：true=保存 false=不保存 null=取消</summary>
    public static Task<bool?> ConfirmSaveAsync(string message)
    {
        var (show, result) = Prepare<bool?>(out var card, out var buttons, out var actions);

        card.Children.Add(MessageBlock(message));
        var save = PrimaryButton(Loc.Get("Save"), danger: false);
        var discard = new Button { Style = Ghost(), Content = Loc.Get("DontSave") };
        var cancel = new Button { Style = Ghost(), Content = Loc.Get("Cancel") };
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 18, 0, 0),
        };
        row.Children.Add(cancel);
        row.Children.Add(new Border { Width = 10 });
        row.Children.Add(discard);
        row.Children.Add(new Border { Width = 10 });
        row.Children.Add(save);
        card.Children.Add(row);

        buttons.Add(save);
        buttons.Add(discard);
        buttons.Add(cancel);
        actions.Add(() => result.TrySetResult(true));
        actions.Add(() => result.TrySetResult(false));
        actions.Add(() => result.TrySetResult(null));

        show(save);
        return result.Task;
    }

    /// <summary>文本输入。返回 null = 取消；输入去空白后为空时确定按钮禁用</summary>
    public static Task<string?> PromptAsync(string title, string prompt, string initial = "")
    {
        var (show, result) = Prepare<string?>(out var card, out var buttons, out var actions);

        card.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = Theme.Brush("TextMain"),
        });
        card.Children.Add(new TextBlock
        {
            Text = prompt,
            FontSize = 12,
            Foreground = Theme.Brush("TextSub"),
            Margin = new Thickness(1, 6, 0, 10),
        });

        var box = new TextBox
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
        card.Children.Add(box);

        var ok = PrimaryButton(Loc.Get("Ok"), danger: false);
        var cancel = new Button { Style = Ghost(), Content = Loc.Get("Cancel") };
        card.Children.Add(ButtonRow(cancel, ok));

        ok.IsEnabled = initial.Trim().Length > 0;
        box.TextChanged += (_, _) => ok.IsEnabled = box.Text.Trim().Length > 0;
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && ok.IsEnabled)
            {
                e.Handled = true;
                ok.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }
        };

        buttons.Add(ok);
        buttons.Add(cancel);
        actions.Add(() => result.TrySetResult(box.Text.Trim()));
        actions.Add(() => result.TrySetResult(null));

        show(box);
        box.SelectAll();
        return result.Task;
    }

    // ---------- 组装 ----------

    static TextBlock MessageBlock(string message) => new()
    {
        Text = message,
        FontSize = 13,
        Foreground = Theme.Brush("TextBody"),
        TextWrapping = TextWrapping.Wrap,
    };

    static Style Ghost() => (Style)Application.Current.FindResource("GhostBtn");

    static Button PrimaryButton(string text, bool danger) => new()
    {
        Style = (Style)Application.Current.FindResource(danger ? "DangerBtn" : "AddBtn"),
        Content = text,
        MinWidth = 84,
    };

    static StackPanel ButtonRow(Button cancel, Button ok)
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 18, 0, 0),
        };
        row.Children.Add(cancel);
        row.Children.Add(new Border { Width = 10 });
        row.Children.Add(ok);
        return row;
    }

    /// <summary>防重入 + 建结果源；返回 show(focus) 一步挂载到主窗口，按钮与结果动作一一对应</summary>
    static (Action<UIElement> show, TaskCompletionSource<T> result) Prepare<T>(
        out StackPanel card, out List<Button> buttons, out List<Action> actions)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        card = new StackPanel();
        buttons = new List<Button>();
        actions = new List<Action>();
        if (Interlocked.CompareExchange(ref _open, 1, 0) != 0)
        {
            // 已有对话框打开（理论到不了）：按取消返回，绝不排队堆叠
            tcs.TrySetResult(default!);
            return ((_ => { }), tcs);
        }
        var c = card; var b = buttons; var a = actions; // out 参数不能被 lambda 捕获
        return ((focus) => Mount(c, b, a, focus), tcs);
    }

    static void Mount(StackPanel card, List<Button> buttons, List<Action> actions, UIElement focus)
    {
        var acts = actions; // out 参数不能被下面的局部函数捕获
        var win = Application.Current.MainWindow;
        var root = win?.Content as Grid;
        if (win == null || root == null)
        {
            _open = 0;
            acts[^1]();
            return;
        }

        var cardBorder = new Border
        {
            Width = 380,
            CornerRadius = new CornerRadius(16),
            Background = Theme.Brush("Card"),
            Padding = new Thickness(22, 18, 22, 18),
            Margin = new Thickness(0, 16, 0, 16),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Child = card,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = new ScaleTransform(0.96, 0.96),
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                BlurRadius = 30, ShadowDepth = 8, Opacity = 0.30, Color = FromHex("#1A2038"),
            },
        };
        cardBorder.SetBinding(FrameworkElement.MaxHeightProperty,
            new System.Windows.Data.Binding("ActualHeight") { Source = win });

        var mask = new Border
        {
            Background = new SolidColorBrush(FromHex("#73161C2E")),
            Opacity = 0,
        };
        var overlay = new Grid();
        overlay.Children.Add(mask);
        overlay.Children.Add(cardBorder);
        Grid.SetRow(overlay, 0);
        Grid.SetRowSpan(overlay, 2);
        Grid.SetColumn(overlay, 0);
        Grid.SetColumnSpan(overlay, 2);
        root.Children.Add(overlay);

        var shownAt = Environment.TickCount64;
        var closing = false;
        void Close(int resultIndex)
        {
            if (closing) return;
            closing = true;
            acts[resultIndex]();
            var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(150));
            fade.Completed += (_, _) =>
            {
                win.PreviewKeyDown -= OnEsc;
                root.Children.Remove(overlay);
                _open = 0;
            };
            mask.BeginAnimation(UIElement.OpacityProperty, fade);
            cardBorder.BeginAnimation(UIElement.OpacityProperty, fade);
        }

        void OnEsc(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                Close(actions.Count - 1); // 约定：最后一个动作永远是取消
            }
        }
        win.PreviewKeyDown += OnEsc;
        mask.MouseLeftButtonUp += (_, _) =>
        {
            if (Environment.TickCount64 - shownAt < 250) return; // 防打开瞬间误触
            Close(actions.Count - 1);
        };

        for (int i = 0; i < buttons.Count; i++)
        {
            var idx = i;
            buttons[i].Click += (_, _) => Close(idx);
        }

        var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180));
        var scale = new DoubleAnimation(0.96, 1, TimeSpan.FromMilliseconds(220))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        mask.BeginAnimation(UIElement.OpacityProperty, fadeIn);
        cardBorder.BeginAnimation(UIElement.OpacityProperty, fadeIn);
        ((ScaleTransform)cardBorder.RenderTransform).BeginAnimation(ScaleTransform.ScaleXProperty, scale);
        ((ScaleTransform)cardBorder.RenderTransform).BeginAnimation(ScaleTransform.ScaleYProperty, scale);

        win.Dispatcher.BeginInvoke(() => focus.Focus(), DispatcherPriority.Input);
    }

    static System.Windows.Media.Color FromHex(string hex) =>
        (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex);
}
