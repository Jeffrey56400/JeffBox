using System.IO;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TodoApp.Models;
using TodoApp.Services;

namespace TodoApp;

/// <summary>
/// 树形待办 ViewModel：子任务与父任务是同一个类型，可无限嵌套。
/// Level/Parent 用于缩进与层级面包屑；数据变更通过 PropertyChanged + Mutated 冒泡通知窗口保存。
/// </summary>
public partial class TodoViewModel : INotifyPropertyChanged
{
    [GeneratedRegex(@"!\[")]
    private static partial Regex ImageRef();

    [GeneratedRegex(@"!\[[^\]]*\]\(([^)\s]+)\)")]
    private static partial Regex FirstImageRef();

    [GeneratedRegex(@"^!\[[^\]]*\]\([^)\s]+\)\s*$", RegexOptions.Multiline)]
    private static partial Regex ImageLineRef();

    public TodoItem Item { get; }
    public TodoViewModel? Parent { get; }
    public int Level { get; }

    public Guid Id => Item.Id;

    public ObservableCollection<TodoViewModel> Children { get; }

    /// <summary>结构性变化（增删子级等）需要保存时触发</summary>
    public event Action? Mutated;

    public TodoViewModel(TodoItem item, TodoViewModel? parent = null, int level = 0)
    {
        Item = item;
        Parent = parent;
        Level = level;
        Children = new ObservableCollection<TodoViewModel>(
            item.Children.Select(c => new TodoViewModel(c, this, level + 1)));
        foreach (var c in Children) HookChild(c);
        Children.CollectionChanged += Children_CollectionChanged;
    }

    // ---------- 子级管理 ----------

    public TodoViewModel AddChild(TodoItem item)
    {
        var child = new TodoViewModel(item, this, Level + 1);
        Children.Add(child);
        return child;
    }

    void Children_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems != null)
            foreach (TodoViewModel c in e.NewItems) HookChild(c);
        if (e.OldItems != null)
            foreach (TodoViewModel c in e.OldItems) UnhookChild(c);
        // 关键：把 VM 树同步回数据模型，否则保存/重启后子任务丢失或复活
        Item.Children = Children.Select(c => c.Item).ToList();
        OnPropertyChanged(nameof(HasSubs));
        OnPropertyChanged(nameof(SubProgress));
        OnPropertyChanged(nameof(PreviewMeta));
        OnPropertyChanged(nameof(ChevronTip));
        Mutated?.Invoke();
    }

    void HookChild(TodoViewModel child) => child.PropertyChanged += Child_PropertyChanged;

    void UnhookChild(TodoViewModel child) => child.PropertyChanged -= Child_PropertyChanged;

    void Child_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IsDone))
        {
            OnPropertyChanged(nameof(SubProgress));
            OnPropertyChanged(nameof(PreviewMeta));
            Mutated?.Invoke();
        }
    }

    /// <summary>深度优先遍历自身与全部后代</summary>
    public IEnumerable<TodoViewModel> Flatten()
    {
        yield return this;
        foreach (var c in Children)
            foreach (var d in c.Flatten())
                yield return d;
    }

    /// <summary>筛选时树内任一节点命中即显示该根</summary>
    public bool TreeMatches(Func<TodoViewModel, bool> predicate) =>
        predicate(this) || Children.Any(c => c.TreeMatches(predicate));

    /// <summary>面包屑：「父任务 › 子任务」链（不含自身）</summary>
    public string Breadcrumb
    {
        get
        {
            if (Parent == null) return "";
            var chain = new List<string>();
            for (var p = Parent; p != null; p = p.Parent)
                chain.Add(p.Text.Length > 12 ? p.Text[..12] + "…" : p.Text);
            chain.Reverse();
            return string.Join(" › ", chain);
        }
    }

    // ---------- 数据属性 ----------

    public string Text
    {
        get => Item.Text;
        set
        {
            var v = value?.Trim() ?? "";
            if (v.Length == 0 || Item.Text == v) return; // 不允许置空标题
            Item.Text = v;
            OnPropertyChanged();
            OnPropertyChanged(nameof(PreviewMeta));
        }
    }

    public string Details
    {
        get => Item.Details ?? "";
        set
        {
            if (Item.Details != value)
            {
                Item.Details = value;
                _firstImage = null;
                OnPropertyChanged();
                OnPropertyChanged(nameof(PreviewDetails));
                OnPropertyChanged(nameof(PreviewMeta));
                OnPropertyChanged(nameof(FirstImage));
            }
        }
    }

    /// <summary>详情中第一张附件图片（悬停预览弹窗显示用），代码加载确保渲染，无图返回 null</summary>
    ImageSource? _firstImage;

    public ImageSource? FirstImage
    {
        get
        {
            if (_firstImage == null)
                _firstImage = LoadFirstImage();
            return _firstImage;
        }
    }

    ImageSource? LoadFirstImage()
    {
        var m = FirstImageRef().Match(Item.Details ?? "");
        if (!m.Success) return null;
        var p = Attachments.GetPath(Id, m.Groups[1].Value);
        if (p == null) return null;
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.DecodePixelWidth = 300;
            bmp.UriSource = new Uri(p);
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch
        {
            return null;
        }
    }

    public bool IsDone
    {
        get => Item.IsDone;
        set
        {
            if (Item.IsDone == value) return;
            Item.IsDone = value;
            Item.CompletedAt = value ? DateTime.Now : null;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsOverdue));
        }
    }

    public int Priority
    {
        get => Item.Priority;
        set { if (Item.Priority != value) { Item.Priority = value; OnPropertyChanged(); OnPropertyChanged(nameof(PreviewMeta)); } }
    }

    public DateTime? DueAt
    {
        get => Item.DueAt;
        set
        {
            if (Item.DueAt != value)
            {
                Item.DueAt = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(DueText));
                OnPropertyChanged(nameof(IsOverdue));
                OnPropertyChanged(nameof(PreviewMeta));
            }
        }
    }

    public bool Remind
    {
        get => Item.Remind;
        set { if (Item.Remind != value) { Item.Remind = value; OnPropertyChanged(); OnPropertyChanged(nameof(PreviewMeta)); } }
    }

    // ---------- 派生文案 ----------

    public bool HasSubs => Children.Count > 0;
    public int SubDone => Children.Count(c => c.IsDone);
    public string SubProgress => HasSubs ? Loc.F("SubProgressFmt", SubDone, Children.Count) : "";

    // 默认展开：子任务创建/加载后立即可见，可手动收起
    bool _isExpanded = true;
    public bool IsExpanded
    {
        get => _isExpanded;
        set { if (_isExpanded != value) { _isExpanded = value; OnPropertyChanged(); } }
    }

    public string DueText
    {
        get
        {
            if (Item.DueAt is not { } d) return "";
            if (d.Date == DateTime.Today) return Loc.F("DueTodayFmt", d.ToString("HH:mm"));
            if (d.Date == DateTime.Today.AddDays(1)) return Loc.F("DueTomorrowFmt", d.ToString("HH:mm"));
            return Loc.Lang == "en"
                ? d.ToString("MMM d HH:mm", System.Globalization.CultureInfo.InvariantCulture)
                : d.ToString("M月d日 HH:mm");
        }
    }

    public bool IsOverdue => !IsDone && Item.DueAt is { } d && d < DateTime.Now;

    string _dueTextSnapshot = "";
    bool _overdueSnapshot;

    /// <summary>定时刷新相对文案（跨零点、到点变红）——递归刷新全树</summary>
    public void RefreshDue()
    {
        foreach (var vm in Flatten())
        {
            if (vm._dueTextSnapshot != vm.DueText) { vm._dueTextSnapshot = vm.DueText; vm.OnPropertyChanged(nameof(DueText)); }
            if (vm._overdueSnapshot != vm.IsOverdue) { vm._overdueSnapshot = vm.IsOverdue; vm.OnPropertyChanged(nameof(IsOverdue)); }
        }
    }

    /// <summary>语言切换后刷新本地化派生文案——递归刷新全树</summary>
    public void RefreshLocale()
    {
        foreach (var vm in Flatten())
        {
            vm._dueTextSnapshot = "";
            vm._firstImage = null;
            vm.OnPropertyChanged(nameof(DueText));
            vm.OnPropertyChanged(nameof(SubProgress));
            vm.OnPropertyChanged(nameof(PreviewDetails));
            vm.OnPropertyChanged(nameof(PreviewMeta));
            vm.OnPropertyChanged(nameof(ChevronTip));
            vm.OnPropertyChanged(nameof(FirstImage));
        }
    }

    public string PreviewDetails
    {
        get
        {
            // 摘要里去掉图片语法（图片由 FirstImage 单独显示）和多余空行
            var text = ImageLineRef().Replace(Details ?? "", "");
            text = Regex.Replace(text, @"\n{2,}", "\n").Trim();
            return text.Length == 0 ? Loc.Get("NoDetails") : text;
        }
    }

    public string PreviewMeta
    {
        get
        {
            var parts = new List<string>();
            if (Priority > 0) parts.Add(Loc.F("MetaPriorityFmt", Loc.PriorityName(Priority)));
            if (Item.DueAt is { }) parts.Add(Loc.F("MetaDueFmt", DueText));
            if (HasSubs) parts.Add(SubProgress);
            if (Remind) parts.Add(Loc.Get("MetaRemindOn"));
            var imgs = ImageRef().Matches(Details).Count;
            if (imgs > 0) parts.Add(Loc.F("MetaImagesFmt", imgs));
            parts.Add(Loc.F("MetaCreatedFmt", CreatedText));
            return string.Join(" · ", parts);
        }
    }

    public string CreatedText => Loc.Lang == "en"
        ? Item.CreatedAt.ToString("MMM d, HH:mm", System.Globalization.CultureInfo.InvariantCulture)
        : Item.CreatedAt.ToString("M月d日 HH:mm");

    public string ChevronTip => HasSubs ? Loc.Get("ChevronTipSubs") : Loc.Get("ChevronTipDetail");

    public event PropertyChangedEventHandler? PropertyChanged;

    void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
