using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using TodoApp.Services;
using Button = System.Windows.Controls.Button;
using UserControl = System.Windows.Controls.UserControl;
using Application = System.Windows.Application;

namespace TodoApp.Views;

/// <summary>
/// 现代化日期选择：字段按钮 + 应用内自绘日历下拉（今天/明天/一周后快捷 + 清除）。
/// 完全替代框架 DatePicker——不受其样式解析与 UIA Peer 竞态影响。
/// 日期与时钟解耦：这里只管日期，时间由旁边的小时/分钟组合框负责。
/// </summary>
public partial class DateField : UserControl
{
    public static readonly DependencyProperty SelectedDateProperty =
        DependencyProperty.Register(nameof(SelectedDate), typeof(DateTime?), typeof(DateField),
            new PropertyMetadata(null, (d, _) => ((DateField)d).RefreshText()));

    /// <summary>选中的日期（日期部分；时间由宿主的小时/分钟组合框负责）</summary>
    public DateTime? SelectedDate
    {
        get => (DateTime?)GetValue(SelectedDateProperty);
        set => SetValue(SelectedDateProperty, value);
    }

    /// <summary>用户挑了日期（或清除）后触发；宿主负责提交业务</summary>
    public event Action? DatePicked;

    DateTime _month = new(DateTime.Now.Year, DateTime.Now.Month, 1);

    public DateField()
    {
        InitializeComponent();
    }

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        RebuildAll();
    }

    /// <summary>宿主把焦点给字段按钮（替代原 DatePicker.Focus 的调用点）</summary>
    public void FocusField() => Field.Focus();

    public void RefreshLocale() => RebuildAll();

    void RebuildAll()
    {
        BuildQuickRow();
        BuildWeekHeader();
        RefreshText();
    }

    void RefreshText()
    {
        if (ValueText == null) return;
        if (SelectedDate is DateTime d)
        {
            ValueText.Text = Loc.Lang == "en"
                ? d.ToString("ddd, MMM d", System.Globalization.CultureInfo.InvariantCulture)
                : d.ToString("M月d日 ddd", new System.Globalization.CultureInfo("zh-CN"));
            ValueText.Foreground = Theme.Brush("TextBody");
        }
        else
        {
            ValueText.Text = Loc.Get("PickDateHint");
            ValueText.Foreground = Theme.Brush("TextFaint");
        }
    }

    // ---------- 弹层 ----------

    void Field_Click(object sender, RoutedEventArgs e)
    {
        var anchor = SelectedDate ?? DateTime.Today;
        _month = new DateTime(anchor.Year, anchor.Month, 1);
        BuildMonth();
        CalPopup.PlacementTarget = Field;
        CalPopup.IsOpen = true;
    }

    void PrevBtn_Click(object sender, RoutedEventArgs e)
    {
        _month = _month.AddMonths(-1);
        BuildMonth();
    }

    void NextBtn_Click(object sender, RoutedEventArgs e)
    {
        _month = _month.AddMonths(1);
        BuildMonth();
    }

    void Pick(DateTime? date)
    {
        SelectedDate = date;
        CalPopup.IsOpen = false;
        DatePicked?.Invoke();
    }

    // ---------- 构建 ----------

    void BuildQuickRow()
    {
        QuickRow.Children.Clear();
        QuickRow.Children.Add(QuickChip(Loc.Get("CalToday"), () => Pick(DateTime.Today)));
        QuickRow.Children.Add(Gap(6));
        QuickRow.Children.Add(QuickChip(Loc.Get("QuickTomorrow"), () => Pick(DateTime.Today.AddDays(1))));
        QuickRow.Children.Add(Gap(6));
        QuickRow.Children.Add(QuickChip(Loc.Get("QuickNextWeek"), () => Pick(DateTime.Today.AddDays(7))));
        var clear = new Button
        {
            Style = (Style)Application.Current.FindResource("GhostBtn"),
            Content = Loc.Get("NoDeadline"),
            FontSize = 11.5,
            Padding = new Thickness(6, 2, 6, 2),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
        };
        clear.Click += (_, _) => Pick(null);
        QuickRow.Children.Add(clear);
    }

    static Border Gap(double w) => new() { Width = w };

    Button QuickChip(string text, Action act)
    {
        var b = new Button
        {
            Style = (Style)Application.Current.FindResource("ChipBtn"),
            Content = text,
            FontSize = 11.5,
            Padding = new Thickness(9, 4, 9, 4),
        };
        b.Click += (_, _) => act();
        return b;
    }

    void BuildWeekHeader()
    {
        WeekHeader.Children.Clear();
        var names = Loc.Lang == "en"
            ? new[] { "Su", "Mo", "Tu", "We", "Th", "Fr", "Sa" }
            : new[] { "日", "一", "二", "三", "四", "五", "六" };
        foreach (var n in names)
            WeekHeader.Children.Add(new TextBlock
            {
                Text = n,
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Foreground = Theme.Brush("TextSub"),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            });
    }

    void BuildMonth()
    {
        TitleText.Text = Loc.Lang == "en"
            ? _month.ToString("yyyy MMMM", System.Globalization.CultureInfo.InvariantCulture)
            : _month.Year + "年" + _month.Month + "月";

        Days.Children.Clear();
        int lead = (int)_month.DayOfWeek; // 周日=0，与表头一致
        for (int i = 0; i < lead; i++)
            Days.Children.Add(new Border { Width = 30, Height = 28 });
        int total = DateTime.DaysInMonth(_month.Year, _month.Month);
        var today = DateTime.Today;
        for (int d = 1; d <= total; d++)
        {
            var date = new DateTime(_month.Year, _month.Month, d);
            var btn = new Button
            {
                Width = 30,
                Height = 28,
                FontSize = 12,
                Content = d.ToString(),
                Cursor = System.Windows.Input.Cursors.Hand,
                Focusable = false,
                Style = (Style)FindResource("DayBtn"),
                Tag = SelectedDate?.Date == date ? "selected"
                    : date == today ? "today" : null,
            };
            var pickDate = date;
            btn.Click += (_, _) => Pick(pickDate);
            Days.Children.Add(btn);
        }
    }
}
