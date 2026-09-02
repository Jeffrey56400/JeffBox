using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using TodoApp.Models;
using TodoApp.Services;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Brush = System.Windows.Media.Brush;
using TextBox = System.Windows.Controls.TextBox;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using DataObject = System.Windows.DataObject;
using DataFormats = System.Windows.DataFormats;
using DataObjectPastingEventArgs = System.Windows.DataObjectPastingEventArgs;
using DragEventArgs = System.Windows.DragEventArgs;

namespace TodoApp;

public partial class MainWindow : Window
{
    enum TodoFilter { All, Active, Done }
    enum ToolTab { Todo, Md, Launch }

    Dictionary<ToolTab, UIElement>? _toolViews;
    ToolTab _tab = ToolTab.Todo;

    static readonly (string Name, string Color)[] Priorities =
    {
        ("无", "#C9CEE0"),
        ("低", "#3DD598"),
        ("中", "#FFB627"),
        ("高", "#FF5A5F"),
    };

    static readonly Dictionary<string, (uint Mods, uint Vk)> Hotkeys = new()
    {
        ["Ctrl+Alt+T"] = (Native.MOD_CONTROL | Native.MOD_ALT | Native.MOD_NOREPEAT, 0x54),
        ["Ctrl+Alt+D"] = (Native.MOD_CONTROL | Native.MOD_ALT | Native.MOD_NOREPEAT, 0x44),
        ["Ctrl+Alt+J"] = (Native.MOD_CONTROL | Native.MOD_ALT | Native.MOD_NOREPEAT, 0x4A),
        ["Alt+Q"] = (Native.MOD_ALT | Native.MOD_NOREPEAT, 0x51),
    };

    const int WM_HOTKEY = 0x0312;
    const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string RunValueName = "JeffBox";
    const string AppVersion = "1.0.3";

    readonly ObservableCollection<TodoViewModel> _all = new();   // 仅根任务
    readonly ObservableCollection<TodoViewModel> _view = new();

    // 完整创建卡：草稿附件目录 + 子任务草稿
    Guid _newDraftId = Guid.NewGuid();
    readonly ObservableCollection<TodoViewModel> _newSubs = new();
    bool _newPreviewOn;

    TodoFilter _filter = TodoFilter.All;
    int _newPriority;
    bool _ready;

    // 悬停 500ms 预览 + 移入弹窗的延迟关闭
    readonly DispatcherTimer _hoverTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    readonly DispatcherTimer _popupCloseTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    Border? _hoverCard;

    // 到期提醒（递归覆盖所有层级）
    readonly DispatcherTimer _reminderTimer = new() { Interval = TimeSpan.FromSeconds(20) };
    readonly HashSet<Guid> _notified = new();
    readonly DispatcherTimer _bannerTimer = new() { Interval = TimeSpan.FromSeconds(8) };
    TodoViewModel? _bannerVm;

    // 详情浮层（实时编辑）
    TodoViewModel? _detailVm;
    bool _populatingDetail;
    bool _notesPreviewOn;
    long _overlayShownAt;      // 防双击误关浮层
    long _settingsShownAt;     // 设置浮层独立计时
    int _bannerGen;            // 横幅动画代际，防止旧动画 Completed 误关新横幅

    // 托盘 / 快捷键 / 设置
    AppSettings _settings = new();
    System.Windows.Forms.NotifyIcon? _tray;
    System.Windows.Forms.ToolStripMenuItem? _trayToggleItem;
    System.Windows.Forms.ToolStripMenuItem? _hotkeyRoot;
    IntPtr _hwnd;
    HwndSource? _hookSource;
    bool _forceExit;
    bool _trayTipShown;

    public MainWindow()
    {
        _settings = AppSettings.Load();
        Loc.Init(_settings.Language);
        Theme.Apply(_settings.Theme);

        InitializeComponent();
        ListItems.ItemsSource = _view;
        _all.CollectionChanged += (_, _) => UpdateStats();

        // 工具页常驻（视图已实例化），启动时恢复上次打开的工具
        _toolViews = new Dictionary<ToolTab, UIElement>
        {
            [ToolTab.Todo] = TodoView,
            [ToolTab.Md] = MdView,
            [ToolTab.Launch] = LaunchView,
        };
        if (App.PendingOpenFile != null)
        {
            // 双击 .md / "打开方式" 直接启动本应用：进入笔记工具的阅读模式
            var f = App.PendingOpenFile;
            App.PendingOpenFile = null;
            SwitchTool(ToolTab.Md);
            MdView.OpenExternal(f);
        }
        else if (_settings.LastTool == "md") SwitchTool(ToolTab.Md);
        else if (_settings.LastTool == "launch") SwitchTool(ToolTab.Launch);
        ConsumeOpenRequest();

        HourCombo.ItemsSource = Enumerable.Range(0, 24).Select(i => $"{i:00}").ToList();
        MinCombo.ItemsSource = Enumerable.Range(0, 12).Select(i => $"{i * 5:00}").ToList();
        NewHourCombo.ItemsSource = HourCombo.ItemsSource;
        NewMinCombo.ItemsSource = MinCombo.ItemsSource;
        NewHourCombo.SelectedIndex = 9;
        NewMinCombo.SelectedIndex = 0;
        NewSubList.ItemsSource = _newSubs;

        // 两个 Markdown 编辑框都支持 Ctrl+V 直接粘贴图片
        DataObject.AddPastingHandler(DetailNotesBox, OnNotesPaste);
        DataObject.AddPastingHandler(NewDetailsBox, OnNotesPaste);

        _hoverTimer.Tick += (_, _) => OpenPreview();
        _popupCloseTimer.Tick += (_, _) =>
        {
            _popupCloseTimer.Stop();
            ClosePreview();
            _hoverCard = null;
        };
        _reminderTimer.Tick += (_, _) => CheckReminders();
        _bannerTimer.Tick += (_, _) => HideBanner();
        // 窗口失活（点桌面/任务栏/Alt+Tab）时收起悬停预览，避免弹窗残留在最前
        Deactivated += (_, _) =>
        {
            _hoverTimer.Stop();
            _popupCloseTimer.Stop();
            ClosePreview();
        };

        ApplyLanguage();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        foreach (var item in TodoStorage.Load())
        {
            var vm = new TodoViewModel(item);
            Attach(vm);
            _all.Add(vm);
        }

        // 启动前就已过期的提醒不再弹横幅，只通过卡片红色胶囊提示
        var now = DateTime.Now;
        foreach (var vm in _all.SelectMany(v => v.Flatten()))
            if (vm.Remind && vm.DueAt is DateTime d && d <= now)
                _notified.Add(vm.Id);

        _ready = true;
        ApplyFilter();
        _reminderTimer.Start();
        if (TodoStorage.LastLoadFailed)
        {
            _trayTipShown = true;
            _tray?.ShowBalloonTip(6000, Loc.Get("DataCorruptTitle"),
                Loc.Get("DataCorruptHint"), System.Windows.Forms.ToolTipIcon.Warning);
        }
        else
        {
            Attachments.CleanupOrphans(_all.Select(v => v.Item));
        }

        // 托盘 + 全局快捷键
        _hwnd = new WindowInteropHelper(this).Handle;
        _hookSource = HwndSource.FromHwnd(_hwnd);
        _hookSource?.AddHook(WndProc);
        RestoreWindowBounds();
        // 打开位置：鼠标屏居中 / 鼠标附近（设置项）。静默启动不定屏，等用户真正呼出再说
        if (!App.StartMinimized) ApplyOpenPosition();
        // 启动即恢复最大化时 OnStateChanged 早于实际尺寸，补偿边距要等布局后再算
        SizeChanged += (_, _) => FixMaximizedMargin();
        ApplyMdAssociation(_settings.MdAssociate);
        InitTray();
        InitHotkeyBoxes();
        ApplyAllHotkeys();
        UpdateCloseTooltip();

        if (App.StartMinimized)
        {
            // OnSourceInitialized 阶段 _isVisible 还是 false，直接 Hide() 是空操作；
            // 排到布局后再隐藏，静默启动才真正不闪窗口
            Dispatcher.BeginInvoke(() =>
            {
                Hide();
                _trayTipShown = true; // 静默启动不弹提示
            });
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        SaveWindowBounds();
        if (_forceExit || !_settings.MinimizeToTray)
        {
            // 真正退出前：MD 有未保存修改时询问
            if (!MdView.ConfirmDiscard())
            {
                e.Cancel = true;
                return;
            }
            return;
        }
        e.Cancel = true;
        HideToTray(showTip: true);
    }

    protected override void OnClosed(EventArgs e)
    {
        Save();
        _settings.Save();
        if (_tray != null)
        {
            _tray.Visible = false;
            _tray.Dispose();
            _tray = null;
        }
        foreach (var id in HkIds)
            Native.UnregisterHotKey(_hwnd, id);
        _hookSource?.RemoveHook(WndProc);
        _hookSource = null;
        base.OnClosed(e);
    }

    // ---------- 树订阅 / 事件 ----------

    void Attach(TodoViewModel vm)
    {
        foreach (var v in vm.Flatten())
        {
            v.PropertyChanged += Vm_PropertyChanged;
            v.Mutated += Vm_Mutated;
        }
    }

    void Detach(TodoViewModel vm)
    {
        foreach (var v in vm.Flatten())
        {
            v.PropertyChanged -= Vm_PropertyChanged;
            v.Mutated -= Vm_Mutated;
        }
    }

    void Vm_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (sender is not TodoViewModel vm) return;
        switch (e.PropertyName)
        {
            case nameof(TodoViewModel.IsDone):
                Save();
                ApplyFilter();
                break;
            case nameof(TodoViewModel.DueAt):
            case nameof(TodoViewModel.Remind):
                _notified.Remove(vm.Id); // 设置变化后允许将来再次提醒
                Save();
                break;
            case nameof(TodoViewModel.Text):
            case nameof(TodoViewModel.Details):
            case nameof(TodoViewModel.Priority):
                Save();
                break;
            // 其余（DueText/SubProgress/PreviewMeta 等派生属性）不落盘
        }
    }

    void Vm_Mutated() => Save();

    // ---------- 添加（一步创建：标题 + 详情/截止/提醒/优先级/子任务） ----------

    void AddTodo()
    {
        var text = InputBox.Text.Trim();
        if (text.Length == 0) return;

        var item = new TodoItem
        {
            Id = _newDraftId,   // 收编创建期间粘贴/插入的附件
            Text = text,
            Details = NewDetailsBox.Text.Trim(),
            Priority = _newPriority,
        };

        if (NewDatePicker.SelectedDate is DateTime dd)
        {
            int h = NewHourCombo.SelectedIndex >= 0 ? NewHourCombo.SelectedIndex : 9;
            int m = NewMinCombo.SelectedIndex >= 0 ? NewMinCombo.SelectedIndex * 5 : 0;
            item.DueAt = dd.Date.AddHours(h).AddMinutes(m);
        }
        item.Remind = NewRemindToggle.IsChecked == true;
        foreach (var s in _newSubs)
            item.Children.Add(new TodoItem { Text = s.Text.Trim(), IsDone = s.IsDone });

        var vm = new TodoViewModel(item);
        Attach(vm);
        _all.Insert(0, vm);
        ApplyFilter();
        Save();

        // 重置创建卡（保留已收编的附件），下次创建用全新草稿目录
        _newDraftId = Guid.NewGuid();
        InputBox.Clear();
        NewDetailsBox.Clear();
        NewSubBox.Clear();
        _newSubs.Clear();
        UpdateNewSubsLabel();
        NewDatePicker.SelectedDate = null;
        NewHourCombo.SelectedIndex = 9;
        NewMinCombo.SelectedIndex = 0;
        NewRemindToggle.IsChecked = false;
        SetNewPriorityRadios(_newPriority);
        SetNewPreview(false);
        NewDetailsPanel.Visibility = Visibility.Collapsed;
        DetailsToggleBtn.Content = Loc.Get("More");
        InputBox.Focus();
    }

    void AddBtn_Click(object sender, RoutedEventArgs e) => AddTodo();

    void InputBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            AddTodo();
        }
    }

    void DetailsToggleBtn_Click(object sender, RoutedEventArgs e)
    {
        if (NewDetailsPanel.Visibility == Visibility.Visible)
        {
            // 收起只隐藏面板，保留已填内容（未提交的草稿附件由下次启动的孤儿清理兜底）
            NewDetailsPanel.Visibility = Visibility.Collapsed;
            DetailsToggleBtn.Content = Loc.Get("More");
            InputBox.Focus();
        }
        else
        {
            SetNewPriorityRadios(_newPriority);
            NewDetailsPanel.Visibility = Visibility.Visible;
            DetailsToggleBtn.Content = Loc.Get("Collapse");
            NewDetailsBox.Focus();
        }
    }

    void SetNewPriorityRadios(int p)
    {
        NewP0.IsChecked = p == 0;
        NewP1.IsChecked = p == 1;
        NewP2.IsChecked = p == 2;
        NewP3.IsChecked = p == 3;
    }

    void NewP_Checked(object sender, RoutedEventArgs e)
    {
        if (NewP0 == null) return; // 模板初始化期
        int p = NewP1.IsChecked == true ? 1 : NewP2.IsChecked == true ? 2 : NewP3.IsChecked == true ? 3 : 0;
        if (p == _newPriority) return;
        _newPriority = p;
        PriorityDot.Fill = BrushOf(Priorities[p].Color);
        PriorityLabel.Text = Loc.PriorityName(p);
    }

    void NewNoDueBtn_Click(object sender, RoutedEventArgs e)
    {
        NewDatePicker.SelectedDate = null;
        NewDatePicker.Focus();
    }

    void NewInsertImageBtn_Click(object sender, RoutedEventArgs e)
        => InsertImageFor(NewDetailsBox, _newDraftId);

    void NewPreviewBtn_Click(object sender, RoutedEventArgs e)
    {
        if (!_newPreviewOn)
        {
            MarkdownLite.Render(NewPreviewPanel, NewDetailsBox.Text, name => Attachments.GetPath(_newDraftId, name));
            SetNewPreview(true);
        }
        else
            SetNewPreview(false);
    }

    void SetNewPreview(bool on)
    {
        _newPreviewOn = on;
        NewEditArea.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        NewPreviewHost.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        NewPreviewBtn.Content = Loc.Get(on ? "Edit" : "Preview");
    }

    void NewSubBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        var text = NewSubBox.Text.Trim();
        if (text.Length == 0) return;
        e.Handled = true;
        _newSubs.Add(new TodoViewModel(new TodoItem { Text = text }));
        NewSubBox.Clear();
        UpdateNewSubsLabel();
        NewSubBox.Focus();
    }

    void UpdateNewSubsLabel()
        => NewSubsLabelTb.Text = $"{Loc.Get("SubtasksLabel")} {_newSubs.Count}";

    // ---------- 图片：插入 / 粘贴 ----------

    void InsertImageFor(TextBox box, Guid todoId)
    {
        var ofd = new OpenFileDialog
        {
            Filter = Loc.Get("FilterImage"),
            Title = Loc.Get("InsertImage"),
        };
        if (ofd.ShowDialog(this) != true) return;

        var stored = Attachments.AddCopy(todoId, ofd.FileName);
        if (stored == null)
        {
            _tray?.ShowBalloonTip(2500, Loc.Get("ImportFailed"), ofd.FileName,
                System.Windows.Forms.ToolTipIcon.Warning);
            return;
        }
        InsertMdImage(box, stored);
    }

    void InsertMdImage(TextBox box, string storedName)
    {
        var insert = $"\n![{Loc.Get("ImageAlt")}]({storedName})\n";
        var caret = box.CaretIndex;
        box.Text = box.Text.Insert(caret, insert);
        box.CaretIndex = caret + insert.Length;
        box.Focus();
    }

    /// <summary>右键粘贴：图片走与 Ctrl+V 相同的 PNG 优先/白底合成逻辑，文本放行默认行为</summary>
    void OnNotesPaste(object sender, DataObjectPastingEventArgs e)
    {
        if (sender is not TextBox box) return;
        try
        {
            var id = ReferenceEquals(box, DetailNotesBox) && _detailVm != null
                ? _detailVm.Id
                : _newDraftId;
            if (TryPasteClipboardImage(box, id))
                e.CancelCommand(); // 已按图片插入，拦下默认的文本/格式粘贴
        }
        catch
        {
            // 剪贴板异常时放行默认粘贴
        }
    }

    /// <summary>Ctrl+V 直接处理：优先剪贴板 PNG 原始字节；位图走白底合成（DIB 丢 alpha 会存成黑色）</summary>
    void NotesBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.V || Keyboard.Modifiers != ModifierKeys.Control) return;
        if (sender is not TextBox box) return;
        try
        {
            var id = ReferenceEquals(box, DetailNotesBox) && _detailVm != null
                ? _detailVm.Id
                : _newDraftId;
            if (TryPasteClipboardImage(box, id))
            {
                e.Handled = true;
                return;
            }
        }
        catch
        {
            // 剪贴板被其他进程占用等 COM 异常：放行默认粘贴，不让应用崩溃
        }
        e.Handled = true;
        try
        {
            if (System.Windows.Clipboard.ContainsText())
            {
                var txt = System.Windows.Clipboard.GetText();
                var caret = box.CaretIndex;
                var selLen = box.SelectionLength;
                box.Text = box.Text.Remove(caret, selLen).Insert(caret, txt);
                box.CaretIndex = caret + txt.Length;
            }
        }
        catch { }
    }

    /// <summary>尝试把剪贴板内容作为图片插入；返回 false 表示剪贴板里没有图片，走文本粘贴</summary>
    bool TryPasteClipboardImage(TextBox box, Guid id)
    {
        // 1) 剪贴板自带 PNG 字节（浏览器/聊天工具复制图片常带），原样保存保真度最高
        var dataObj = System.Windows.Clipboard.GetDataObject();
        if (dataObj != null)
        {
            foreach (var fmt in new[] { "PNG", "image/png" })
            {
                if (dataObj.GetDataPresent(fmt))
                {
                    try
                    {
                        if (dataObj.GetData(fmt) is MemoryStream ms)
                        {
                            var bytes = ms.ToArray();
                            if (bytes.Length > 8)
                            {
                                var name = Attachments.AddCopyBytes(id, bytes, ".png");
                                if (name != null) InsertMdImage(box, name);
                                return true;
                            }
                        }
                    }
                    catch { }
                }
            }
        }

        // 2) 位图（截图工具等）：合成白底保存，避免透明区域变黑
        if (System.Windows.Clipboard.ContainsImage())
        {
            if (System.Windows.Clipboard.GetImage() is { } bmp)
            {
                var (name, blank) = SaveBitmapTo(id, bmp);
                if (name != null)
                {
                    if (blank)
                        _tray?.ShowBalloonTip(4000, Loc.Get("PasteBlankTitle"),
                            Loc.Get("PasteBlankHint"), System.Windows.Forms.ToolTipIcon.Warning);
                    InsertMdImage(box, name);
                }
                return true;
            }
            return true; // 有图但取不出来：不再当文本处理
        }

        // 3) 复制的图片文件（资源管理器多选）
        if (System.Windows.Clipboard.ContainsFileDropList())
        {
            var imgs = System.Windows.Clipboard.GetFileDropList().Cast<string>().Where(IsImageFile).ToList();
            if (imgs.Count > 0)
            {
                foreach (var file in imgs)
                {
                    var name = Attachments.AddCopy(id, file);
                    if (name != null) InsertMdImage(box, name);
                }
                return true;
            }
        }

        return false;
    }

    /// <summary>返回 (存储文件名, 是否为空白图)。空白 = 全透明或全白，多半是来源应用没给出有效图像数据</summary>
    (string? Name, bool Blank) SaveBitmapTo(Guid todoId, BitmapSource src)
    {
        try
        {
            var w = src.PixelWidth;
            var h = src.PixelHeight;
            if (w == 0 || h == 0) return (null, true);

            // 剪贴板 DIB 的 alpha 字节常常没被填（恒 0），而 RGB 是完好的。
            // 先检查：全透明但 RGB 有内容 → 丢弃 alpha 按不透明保存，否则整张图会“看不见”。
            var bgra = new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
            var pixels = new byte[w * h * 4];
            bgra.CopyPixels(pixels, w * 4, 0);

            bool anyAlpha = false;
            bool anyRgb = false;
            for (int i = 0; i < pixels.Length; i += 4)
            {
                if (pixels[i + 3] != 0) anyAlpha = true;
                if (pixels[i] != 0 || pixels[i + 1] != 0 || pixels[i + 2] != 0) anyRgb = true;
                if (anyAlpha && anyRgb) break;
            }

            BitmapSource final;
            if (!anyAlpha && anyRgb)
            {
                // 假透明：BGR→RGB 重排为 24 位不透明图
                var rgb = new byte[w * h * 3];
                for (int i = 0, j = 0; i < pixels.Length; i += 4, j += 3)
                {
                    rgb[j] = pixels[i + 2];
                    rgb[j + 1] = pixels[i + 1];
                    rgb[j + 2] = pixels[i];
                }
                final = BitmapSource.Create(w, h, 96, 96, PixelFormats.Rgb24, null, rgb, w * 3);
            }
            else
            {
                // 正常图：合成白底，保留真实透明区域的语义
                var dv = new DrawingVisual();
                using (var dc = dv.RenderOpen())
                {
                    dc.DrawRectangle(System.Windows.Media.Brushes.White, null, new Rect(0, 0, w, h));
                    dc.DrawImage(src, new Rect(0, 0, w, h));
                }
                var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
                rtb.Render(dv);
                final = rtb;
            }

            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(final));
            using var ms = new MemoryStream();
            enc.Save(ms);

            // 空白检测：最终图全透明或全白 → 提示用户剪贴板里可能没有有效图像
            var check = new FormatConvertedBitmap(final, PixelFormats.Bgra32, null, 0);
            var checkBuf = new byte[w * h * 4];
            check.CopyPixels(checkBuf, w * 4, 0);
            bool blank = true;
            for (int i = 0; i < checkBuf.Length; i += 4)
            {
                bool transparent = checkBuf[i + 3] == 0;
                bool white = checkBuf[i] >= 250 && checkBuf[i + 1] >= 250 && checkBuf[i + 2] >= 250;
                if (!transparent && !white) { blank = false; break; }
            }

            return (Attachments.AddCopyBytes(todoId, ms.ToArray(), ".png"), blank);
        }
        catch
        {
            return (null, true);
        }
    }

    static bool IsImageFile(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext is ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".webp";
    }

    void NewDetailsBox_KeyDown(object sender, KeyEventArgs e)
    {
        // Ctrl+Enter 在多行详情输入中直接提交整条任务
        if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            e.Handled = true;
            AddTodo();
        }
    }

    void PriorityBtn_Click(object sender, RoutedEventArgs e)
    {
        _newPriority = (_newPriority + 1) % Priorities.Length;
        PriorityDot.Fill = BrushOf(Priorities[_newPriority].Color);
        PriorityLabel.Text = Loc.PriorityName(_newPriority);
    }

    // ---------- 卡片：悬停预览 + 点击详情 + 子级展开 ----------

    void Card_MouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is Border card)
        {
            // 从一张卡片移到另一张时，先关掉上一张的弹窗并取消挂起的关闭，避免竞态
            _popupCloseTimer.Stop();
            if (_hoverCard != null && !ReferenceEquals(_hoverCard, card))
                ClosePreview();
            _hoverCard = card;
            _hoverTimer.Stop();
            _hoverTimer.Start();
        }
    }

    void Card_MouseLeave(object sender, MouseEventArgs e)
    {
        _hoverTimer.Stop();
        // 延迟关闭：给鼠标移入预览弹窗留时间
        _popupCloseTimer.Stop();
        _popupCloseTimer.Start();
    }

    void PreviewPopup_Enter(object sender, MouseEventArgs e) => _popupCloseTimer.Stop();

    void PreviewPopup_Leave(object sender, MouseEventArgs e)
    {
        _popupCloseTimer.Stop();
        ClosePreview();
        _hoverCard = null;
    }

    void Card_Click(object sender, MouseButtonEventArgs e)
    {
        _hoverTimer.Stop();
        ClosePreview();
        if (sender is Border { DataContext: TodoViewModel vm })
        {
            OpenDetail(vm);
            e.Handled = true;
        }
    }

    void ChevronBtn_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: TodoViewModel vm })
        {
            if (vm.HasSubs)
                vm.IsExpanded = !vm.IsExpanded;
            else
                OpenDetail(vm);
        }
    }

    void OpenPreview()
    {
        if (_hoverCard == null) return;
        if (FindChild<Popup>(_hoverCard) is { } popup)
            popup.IsOpen = true;
    }

    void ClosePreview()
    {
        if (_hoverCard == null) return;
        if (FindChild<Popup>(_hoverCard) is { } popup)
            popup.IsOpen = false;
    }

    // ---------- 详情浮层（实时编辑，任意层级） ----------

    void OpenDetail(TodoViewModel vm)
    {
        _detailVm = vm;
        _populatingDetail = true;

        DetailOverlay.DataContext = vm;
        BreadcrumbTb.Text = vm.Parent == null ? "" : Loc.F("BreadcrumbFmt", vm.Breadcrumb);
        BreadcrumbTb.Visibility = vm.Parent == null ? Visibility.Collapsed : Visibility.Visible;

        DueDatePicker.SelectedDate = vm.DueAt?.Date;
        HourCombo.SelectedIndex = vm.DueAt?.Hour ?? 9;
        MinCombo.SelectedIndex = vm.DueAt is { } d ? Math.Clamp((int)Math.Round(d.Minute / 5.0), 0, 11) : 0;
        SetPriorityRadio(vm.Priority);

        OverlaySubList.ItemsSource = vm.Children;
        SubtasksLabelTb.Text = $"{Loc.Get("SubtasksLabel")} {vm.Children.Count}";
        AddSubtaskBox.Clear();

        DetailMetaText.Text = Loc.F("CreatedAtFmt", vm.CreatedText);
        SetNotesPreview(false);
        _populatingDetail = false;

        DetailOverlay.Visibility = Visibility.Visible;
        _overlayShownAt = Environment.TickCount64;
        var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180));
        var scale = new DoubleAnimation(0.96, 1, TimeSpan.FromMilliseconds(220))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        OverlayMask.BeginAnimation(UIElement.OpacityProperty, fade);
        OverlayCard.BeginAnimation(UIElement.OpacityProperty, fade);
        OverlayScale.BeginAnimation(ScaleTransform.ScaleXProperty, scale);
        OverlayScale.BeginAnimation(ScaleTransform.ScaleYProperty, scale);

        DetailTitleBox.Focus();
        DetailTitleBox.CaretIndex = DetailTitleBox.Text.Length;
    }

    void CloseDetail()
    {
        // 标题/详情是 LostFocus 触发更新：Esc、遮罩、✕ 都不会转移焦点，
        // 必须在这里强制写回数据源，否则最后一段编辑会丢
        DetailTitleBox.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        DetailNotesBox.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        Save();
        DetailOverlay.Visibility = Visibility.Collapsed;
        _detailVm = null;
    }

    void DueDatePicker_SelectedDateChanged(object sender, SelectionChangedEventArgs e) => CommitDue();

    void TimeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) => CommitDue();

    void CommitDue()
    {
        if (_populatingDetail || _detailVm == null) return;
        DateTime? due = DueDatePicker.SelectedDate;
        if (due is DateTime dd)
        {
            int h = HourCombo.SelectedIndex >= 0 ? HourCombo.SelectedIndex : 9;
            int m = MinCombo.SelectedIndex >= 0 ? MinCombo.SelectedIndex * 5 : 0;
            due = dd.Date.AddHours(h).AddMinutes(m);
        }
        _detailVm.DueAt = due;
    }

    void DetailP_Checked(object sender, RoutedEventArgs e)
    {
        if (_populatingDetail || _detailVm == null) return;
        _detailVm.Priority = CheckedPriority();
    }

    int CheckedPriority()
    {
        if (DetailP1.IsChecked == true) return 1;
        if (DetailP2.IsChecked == true) return 2;
        if (DetailP3.IsChecked == true) return 3;
        return 0;
    }

    void SetPriorityRadio(int p)
    {
        DetailP0.IsChecked = p == 0;
        DetailP1.IsChecked = p == 1;
        DetailP2.IsChecked = p == 2;
        DetailP3.IsChecked = p == 3;
    }

    void OverlaySubDelete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: TodoViewModel sub }) return;
        // 详情浮层子任务 vs 创建卡子任务草稿，按归属分流
        if (_detailVm != null && _detailVm.Children.Contains(sub))
        {
            RemoveVm(sub);
            SubtasksLabelTb.Text = $"{Loc.Get("SubtasksLabel")} {_detailVm.Children.Count}";
        }
        else if (_newSubs.Contains(sub))
        {
            _newSubs.Remove(sub);
            UpdateNewSubsLabel();
        }
    }

    void AddSubtaskBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || _detailVm == null) return;
        var text = AddSubtaskBox.Text.Trim();
        if (text.Length == 0) return;
        e.Handled = true;
        var child = _detailVm.AddChild(new TodoItem { Text = text });
        Attach(child);
        Save();
        AddSubtaskBox.Clear();
        SubtasksLabelTb.Text = $"{Loc.Get("SubtasksLabel")} {_detailVm.Children.Count}";
        AddSubtaskBox.Focus();
    }

    void SetNotesPreview(bool on)
    {
        _notesPreviewOn = on;
        DetailNotesBox.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        NotesPreviewHost.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        PreviewBtn.Content = Loc.Get(on ? "Edit" : "Preview");
    }

    void InsertImageBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_detailVm != null)
            InsertImageFor(DetailNotesBox, _detailVm.Id);
    }

    void ImportMdBtn_Click(object sender, RoutedEventArgs e)
    {
        var ofd = new OpenFileDialog
        {
            Filter = Loc.Get("FilterMd"),
            Title = Loc.Get("ImportMd"),
        };
        if (ofd.ShowDialog(this) != true) return;
        try
        {
            DetailNotesBox.Text = File.ReadAllText(ofd.FileName);
            DetailNotesBox.CaretIndex = DetailNotesBox.Text.Length;
            DetailNotesBox.Focus();
        }
        catch
        {
            _tray?.ShowBalloonTip(2500, Loc.Get("ImportFailed"), ofd.FileName,
                System.Windows.Forms.ToolTipIcon.Warning);
        }
    }

    void PreviewBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_detailVm == null) return;
        if (!_notesPreviewOn)
        {
            DetailNotesBox.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
            MarkdownLite.Render(NotesPreviewPanel, DetailNotesBox.Text, name => Attachments.GetPath(_detailVm.Id, name));
            SetNotesPreview(true);
        }
        else
            SetNotesPreview(false);
    }

    void OverlayMask_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (Environment.TickCount64 - _overlayShownAt < 400) return; // 防双击误关
        CloseDetail();
    }

    void DetailCloseBtn_Click(object sender, RoutedEventArgs e) => CloseDetail();

    void DetailDoneBtn_Click(object sender, RoutedEventArgs e) => CloseDetail();

    void DetailDeleteBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_detailVm is { } vm)
        {
            CloseDetail();
            RemoveVm(vm);
        }
    }

    void ClearDueBtn_Click(object sender, RoutedEventArgs e)
    {
        DueDatePicker.SelectedDate = null;
        DueDatePicker.Focus();
    }

    void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        if (DetailOverlay.Visibility == Visibility.Visible)
        {
            CloseDetail();
            e.Handled = true;
        }
        else if (SettingsOverlay.Visibility == Visibility.Visible)
        {
            CloseSettings();
            e.Handled = true;
        }
    }

    /// <summary>预览弹窗不捕获鼠标；点击弹窗本身时放行给弹层的打开处理</summary>
    void Window_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject src && FindParent<Popup>(src) != null)
            return; // 点击的是悬停预览弹窗，交给它自己的处理
        _hoverTimer.Stop();
        ClosePreview();
    }

    /// <summary>点击悬停预览弹窗 = 打开对应待办的详情</summary>
    void PreviewPopup_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border b || b.DataContext is not TodoViewModel vm) return;
        _hoverTimer.Stop();
        _hoverCard = null;
        if (b.Parent is Popup pp) pp.IsOpen = false;
        OpenDetail(vm);
        e.Handled = true;
    }

    // ---------- 一键展开 / 收起 ----------

    void ToggleAllBtn_Click(object sender, RoutedEventArgs e)
    {
        var target = !AllExpanded();
        foreach (var v in _all.SelectMany(v => v.Flatten()))
            if (v.HasSubs)
                v.IsExpanded = target;
        UpdateToggleAllBtn();
    }

    bool AllExpanded() =>
        _all.SelectMany(v => v.Flatten()).All(v => !v.HasSubs || v.IsExpanded);

    void UpdateToggleAllBtn()
    {
        var anySubs = _all.SelectMany(v => v.Flatten()).Any(v => v.HasSubs);
        ToggleAllBtn.Visibility = anySubs ? Visibility.Visible : Visibility.Collapsed;
        ToggleAllBtn.Content = Loc.Get(AllExpanded() ? "CollapseAll" : "ExpandAll");
    }

    // ---------- 删除（树形：递归清附件与提醒状态） ----------

    void DeleteBtn_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: TodoViewModel vm })
            RemoveVm(vm);
    }

    void RemoveVm(TodoViewModel vm)
    {
        foreach (var d in vm.Flatten())
        {
            Detach(d);
            Attachments.DeleteAll(d.Id);
            _notified.Remove(d.Id);
        }
        if (vm.Parent != null)
            vm.Parent.Children.Remove(vm);   // 触发父级 Mutated → 保存
        else
            _all.Remove(vm);
        Save();
        UpdateStats();
        if (vm.Parent == null) ApplyFilter();
    }

    void ClearDoneBtn_Click(object sender, RoutedEventArgs e)
    {
        foreach (var vm in _all.Where(v => v.IsDone).ToList())
            RemoveVm(vm);
        Save();
        ApplyFilter();
    }

    // ---------- 筛选（树内任一节点命中即显示该根） ----------

    void Filter_Checked(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        _filter = sender == FilterActive ? TodoFilter.Active
                : sender == FilterDone ? TodoFilter.Done
                : TodoFilter.All;
        ApplyFilter();
    }

    void ApplyFilter()
    {
        _view.Clear();
        foreach (var vm in _all.Where(MatchesRoot))
            _view.Add(vm);
        UpdateStats();
    }

    bool MatchesRoot(TodoViewModel vm) => _filter switch
    {
        TodoFilter.Active => vm.TreeMatches(v => !v.IsDone),
        TodoFilter.Done => vm.TreeMatches(v => v.IsDone),
        _ => true,
    };

    // ---------- 状态刷新 ----------

    void UpdateStats()
    {
        var total = _all.Count;
        var done = _all.Count(v => v.IsDone);

        FilterAll.Content = $"{Loc.Get("FilterAll")} {total}";
        FilterActive.Content = $"{Loc.Get("FilterActive")} {total - done}";
        FilterDone.Content = $"{Loc.Get("FilterDone")} {done}";

        DoneText.Text = Loc.F("DoneFmt", done, total);
        DoneBar.Value = total == 0 ? 0 : done * 100.0 / total;
        ClearDoneBtn.Visibility = done > 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyState.Visibility = _view.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateToggleAllBtn();
    }

    void Save() => TodoStorage.Save(_all.Select(v => v.Item));

    static Brush BrushOf(string hex)
    {
        var brush = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }

    static T? FindChild<T>(DependencyObject? parent) where T : DependencyObject
    {
        if (parent == null) return null;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T found) return found;
            if (FindChild<T>(child) is { } nested) return nested;
        }
        return null;
    }

    static T? FindParent<T>(DependencyObject? o) where T : DependencyObject
    {
        while (o != null)
        {
            if (o is T found) return found;
            o = VisualTreeHelper.GetParent(o) ??
                (o is FrameworkElement or FrameworkContentElement
                    ? LogicalTreeHelper.GetParent(o)
                    : null);
        }
        return null;
    }

    // ---------- 到期提醒 ----------

    void CheckReminders()
    {
        foreach (var vm in _all.SelectMany(v => v.Flatten()).ToList())
        {
            vm.RefreshDue();
            if (vm.IsDone || !vm.Remind || vm.DueAt is not DateTime due) continue;
            if (due <= DateTime.Now && !_notified.Contains(vm.Id))
            {
                _notified.Add(vm.Id);
                ShowBanner(vm);
            }
        }
    }

    void ShowBanner(TodoViewModel vm)
    {
        _bannerVm = vm;
        _bannerGen++;
        var msg = Loc.F("OverdueBannerFmt", vm.Text);

        if (!IsVisible)
        {
            _tray?.ShowBalloonTip(4000, Loc.Get("ReminderBannerTitle"), msg, System.Windows.Forms.ToolTipIcon.Info);
            return;
        }

        BannerMsg.Text = msg;
        BannerHost.Visibility = Visibility.Visible;

        var slide = new DoubleAnimation(-70, 0, TimeSpan.FromMilliseconds(300))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(300));
        BannerShift.BeginAnimation(TranslateTransform.YProperty, slide);
        BannerCard.BeginAnimation(UIElement.OpacityProperty, fade);
        _bannerTimer.Stop();
        _bannerTimer.Start();
    }

    void HideBanner()
    {
        _bannerTimer.Stop();
        _bannerGen++;
        var gen = _bannerGen;
        var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(220));
        fade.Completed += (_, _) =>
        {
            if (gen == _bannerGen) // 期间若弹出新横幅则不动
                BannerHost.Visibility = Visibility.Collapsed;
        };
        BannerCard.BeginAnimation(UIElement.OpacityProperty, fade);
    }

    void BannerViewBtn_Click(object sender, RoutedEventArgs e)
    {
        HideBanner();
        if (_bannerVm is { } vm) OpenDetail(vm);
    }

    void BannerCloseBtn_Click(object sender, RoutedEventArgs e) => HideBanner();

    // ---------- 托盘 ----------

    void InitTray()
    {
        _tray = new System.Windows.Forms.NotifyIcon
        {
            Icon = LoadAppIcon(),
            ContextMenuStrip = BuildTrayMenu(),
            Visible = true,
        };
        _tray.DoubleClick += (_, _) => ShowFromTray();
        _tray.MouseClick += (_, me) =>
        {
            if (me.Button == System.Windows.Forms.MouseButtons.Left)
                ShowFromTray();
        };
        _tray.BalloonTipClicked += (_, _) => ShowFromTray();
        UpdateTrayText();
    }

    System.Windows.Forms.ContextMenuStrip BuildTrayMenu()
    {
        var open = new System.Windows.Forms.ToolStripMenuItem(Loc.Get("TrayOpen"))
        {
            Font = new System.Drawing.Font("Segoe UI", 9F, System.Drawing.FontStyle.Bold),
        };
        open.Click += (_, _) => ShowFromTray();

        _trayToggleItem = new System.Windows.Forms.ToolStripMenuItem(Loc.Get("TrayMinToggle"))
        {
            CheckOnClick = true,
            Checked = _settings.MinimizeToTray,
        };
        _trayToggleItem.Click += (_, _) =>
        {
            _settings.MinimizeToTray = _trayToggleItem.Checked;
            _settings.Save();
            UpdateCloseTooltip();
            SyncCloseBehavior();
        };

        _hotkeyRoot = new System.Windows.Forms.ToolStripMenuItem(Loc.Get("TrayHotkey"));
        foreach (var name in Hotkeys.Keys)
        {
            var id = name;
            var item = new System.Windows.Forms.ToolStripMenuItem(name) { Tag = id };
            item.Click += (_, _) => SetHotkey(id);
            _hotkeyRoot.DropDownItems.Add(item);
        }
        _hotkeyRoot.DropDownItems.Add(new System.Windows.Forms.ToolStripSeparator());
        var offItem = new System.Windows.Forms.ToolStripMenuItem(Loc.Get("TrayHotkeyOff")) { Tag = "" };
        offItem.Click += (_, _) => SetHotkey("");
        _hotkeyRoot.DropDownItems.Add(offItem);
        SyncHotkeyChecks();

        var exit = new System.Windows.Forms.ToolStripMenuItem(Loc.Get("TrayExit"));
        exit.Click += (_, _) =>
        {
            _forceExit = true;
            Close();
        };

        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add(open);
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add(_trayToggleItem);
        menu.Items.Add(_hotkeyRoot);
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add(exit);
        return menu;
    }

    static System.Drawing.Icon LoadAppIcon()
    {
        try
        {
            var sri = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/app.ico"));
            return new System.Drawing.Icon(sri.Stream);
        }
        catch
        {
            return System.Drawing.SystemIcons.Application;
        }
    }

    public void ShowFromTray()
    {
        // 所有呼出方式（全局热键/直达热键/托盘/快捷方式二次启动）的总入口。
        // 顺序很关键：必须先把窗口真正显示并还原成 Normal（最小化时 GetWindowRect 是
        // (-32000,-32000) 假坐标、隐藏时 WindowState 只是缓存值），物理像素定位才有效
        Show();
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        ApplyOpenPosition();
        Activate();
        Native.SetForegroundWindow(_hwnd);
        InputBox.Focus();
    }

    void HideToTray(bool showTip)
    {
        Hide();
        if (showTip && !_trayTipShown && _tray != null)
        {
            _trayTipShown = true;
            var hint = !string.IsNullOrEmpty(_settings.Hotkey) && _slotOk[(int)HkSlot.Main]
                ? Loc.F("TrayRunningFmt", _settings.Hotkey)
                : Loc.Get("TrayRunningTitle");
            _tray.ShowBalloonTip(2500, Loc.Get("TrayRunningTitle"), hint, System.Windows.Forms.ToolTipIcon.Info);
        }
    }

    void ToggleWindow()
    {
        if (IsVisible && IsActive)
            HideToTray(showTip: false);
        else
            ShowFromTray();
    }

    void UpdateCloseTooltip()
        => WinCloseBtn.ToolTip = Loc.Get(_settings.MinimizeToTray ? "CloseTipTray" : "CloseTipExit");

    // ---------- 全局快捷键：主热键 + 三工具直达，共四槽位 ----------

    enum HkSlot { Main = 0, Todo = 1, Md = 2, Launch = 3 }

    static readonly int[] HkIds = { 0x3230, 0x3231, 0x3232, 0x3233 };
    readonly bool[] _slotOk = new bool[4];

    string SlotText(HkSlot s) => s switch
    {
        HkSlot.Main => _settings.Hotkey,
        HkSlot.Todo => _settings.HotkeyTodo,
        HkSlot.Md => _settings.HotkeyMd,
        HkSlot.Launch => _settings.HotkeyLaunch,
        _ => "",
    };

    void SetSlotText(HkSlot s, string v)
    {
        switch (s)
        {
            case HkSlot.Main: _settings.Hotkey = v; break;
            case HkSlot.Todo: _settings.HotkeyTodo = v; break;
            case HkSlot.Md: _settings.HotkeyMd = v; break;
            case HkSlot.Launch: _settings.HotkeyLaunch = v; break;
        }
    }

    static string SlotLabel(HkSlot s) => Loc.Get(s switch
    {
        HkSlot.Main => "HotkeyMainLabel",
        HkSlot.Todo => "HotkeyTodoLabel",
        HkSlot.Md => "HotkeyMdLabel",
        _ => "HotkeyLaunchLabel",
    });

    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY)
        {
            var id = wParam.ToInt32();
            if (id == HkIds[(int)HkSlot.Main]) { ToggleWindow(); handled = true; }
            else if (id == HkIds[(int)HkSlot.Todo]) { SummonTool(ToolTab.Todo); handled = true; }
            else if (id == HkIds[(int)HkSlot.Md]) { SummonTool(ToolTab.Md); handled = true; }
            else if (id == HkIds[(int)HkSlot.Launch]) { SummonTool(ToolTab.Launch); handled = true; }
        }
        return IntPtr.Zero;
    }

    /// <summary>工具直达：窗口隐藏/最小化→呼出并切换；已在目标页→收起；在其他页→切换。
    /// 最小化的窗口 IsVisible 仍是 true，必须一并视作"未呼出"，否则同页热键会把它藏掉而不是还原</summary>
    void SummonTool(ToolTab tab)
    {
        if (!IsVisible || WindowState == WindowState.Minimized) { ShowFromTray(); SwitchTool(tab); }
        else if (tab == _tab) Hide();
        else SwitchTool(tab);
        // 热键呼出意味着用户要立刻用工具，浮层不应挡着
        if (SettingsOverlay.Visibility == Visibility.Visible) CloseSettings();
        if (DetailOverlay.Visibility == Visibility.Visible) CloseDetail();
    }

    void InitHotkeyBoxes()
    {
        HkMain.LabelKey = "HotkeyMainLabel";
        HkTodo.LabelKey = "HotkeyTodoLabel";
        HkMd.LabelKey = "HotkeyMdLabel";
        HkLaunch.LabelKey = "HotkeyLaunchLabel";
        HkMain.Apply += t => TrySetSlot(HkSlot.Main, t);
        HkTodo.Apply += t => TrySetSlot(HkSlot.Todo, t);
        HkMd.Apply += t => TrySetSlot(HkSlot.Md, t);
        HkLaunch.Apply += t => TrySetSlot(HkSlot.Launch, t);
        HkMain.Disable += () => TrySetSlot(HkSlot.Main, "");
        HkTodo.Disable += () => TrySetSlot(HkSlot.Todo, "");
        HkMd.Disable += () => TrySetSlot(HkSlot.Md, "");
        HkLaunch.Disable += () => TrySetSlot(HkSlot.Launch, "");
        // 捕获时吞掉与自己其他槽位重复的组合，避免按键真实触发那个槽
        HkMain.ShouldSwallow = t => DupInOtherSlot(HkSlot.Main, t);
        HkTodo.ShouldSwallow = t => DupInOtherSlot(HkSlot.Todo, t);
        HkMd.ShouldSwallow = t => DupInOtherSlot(HkSlot.Md, t);
        HkLaunch.ShouldSwallow = t => DupInOtherSlot(HkSlot.Launch, t);
    }

    bool DupInOtherSlot(HkSlot self, string text) =>
        !string.IsNullOrEmpty(text) && Enum.GetValues<HkSlot>().Any(o =>
            o != self && SlotText(o).Equals(text, StringComparison.OrdinalIgnoreCase));

    /// <summary>注册槽位当前热键（不落盘）。空串视为成功停用。</summary>
    bool RegisterSlot(HkSlot s)
    {
        var id = HkIds[(int)s];
        Native.UnregisterHotKey(_hwnd, id);
        var text = SlotText(s);
        if (string.IsNullOrEmpty(text))
        {
            _slotOk[(int)s] = false;
            return true;
        }
        if (!HotkeyInfo.TryParse(text, out var mods, out var vk))
        {
            _slotOk[(int)s] = false;
            return false;
        }
        _slotOk[(int)s] = Native.RegisterHotKey(_hwnd, id, mods | Native.MOD_NOREPEAT, vk);
        return _slotOk[(int)s];
    }

    void ApplyAllHotkeys()
    {
        foreach (HkSlot s in Enum.GetValues<HkSlot>())
            RegisterSlot(s);
        var main = SlotText(HkSlot.Main);
        if (!_slotOk[(int)HkSlot.Main] && !string.IsNullOrEmpty(main) && _tray != null)
            _tray.ShowBalloonTip(3000, Loc.Get("HotkeyFailTitle"),
                HotkeyInfo.ConflictHint(main), System.Windows.Forms.ToolTipIcon.Warning);
        RefreshHotkeyUi();
        SyncHotkeyChecks();
    }

    /// <summary>
    /// 用户录入后尝试启用。失败时回滚旧热键并恢复注册（回滚后 _slotOk 会变回 true，
    /// 判定必须用返回值）。返回 null 表示成功，否则返回给用户看的原因。
    /// </summary>
    string? TrySetSlot(HkSlot s, string text)
    {
        // 一个组合只能绑一个功能：先查其他槽位
        if (!string.IsNullOrEmpty(text))
            foreach (HkSlot o in Enum.GetValues<HkSlot>())
                if (o != s && SlotText(o).Equals(text, StringComparison.OrdinalIgnoreCase))
                    return Loc.F("HotkeySlotDupFmt", SlotLabel(o));

        var old = SlotText(s);
        SetSlotText(s, text);
        if (RegisterSlot(s))
        {
            _settings.Save();
            RefreshHotkeyUi();
            SyncHotkeyChecks();
            return null;
        }

        SetSlotText(s, old);
        RegisterSlot(s); // 恢复旧热键，保证原有功能不被打断
        _settings.Save();
        RefreshHotkeyUi();
        SyncHotkeyChecks();

        var reason = HotkeyInfo.ConflictHint(text);
        if (s == HkSlot.Main && _tray != null)
            _tray.ShowBalloonTip(3000, Loc.Get("HotkeyFailTitle"),
                reason, System.Windows.Forms.ToolTipIcon.Warning);
        return reason;
    }

    void RefreshHotkeyUi()
    {
        UpdateTrayText();
        UpdateHotkeyPill();
        HkMain?.SetHotkey(SlotText(HkSlot.Main), _slotOk[(int)HkSlot.Main]);
        HkTodo?.SetHotkey(SlotText(HkSlot.Todo), _slotOk[(int)HkSlot.Todo]);
        HkMd?.SetHotkey(SlotText(HkSlot.Md), _slotOk[(int)HkSlot.Md]);
        HkLaunch?.SetHotkey(SlotText(HkSlot.Launch), _slotOk[(int)HkSlot.Launch]);
    }

    void UpdateTrayText()
    {
        if (_tray == null) return;
        _tray.Text = string.IsNullOrEmpty(_settings.Hotkey)
            ? Title
            : $"{Title} · {_settings.Hotkey}";
    }

    // 托盘快捷菜单（主热键快捷项）
    void SetHotkey(string id) => TrySetSlot(HkSlot.Main, id);

    void UpdateHotkeyPill()
    {
        var id = _settings.Hotkey;
        var has = !string.IsNullOrEmpty(id);
        HotkeyPillText.Text = has ? id : "";
        HotkeyPill.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        HotkeyPill.ToolTip = has && !_slotOk[(int)HkSlot.Main]
            ? HotkeyInfo.ConflictHint(id)
            : Loc.Get("HotkeyPillTip");
    }

    void SyncHotkeyChecks()
    {
        if (_hotkeyRoot != null)
            foreach (var raw in _hotkeyRoot.DropDownItems)
                if (raw is System.Windows.Forms.ToolStripMenuItem item && item.Tag is string tag)
                    item.Checked = tag == _settings.Hotkey;
    }

    void SyncCloseBehavior()
    {
        if (CloseTrayRadio == null) return;
        CloseTrayRadio.IsChecked = _settings.MinimizeToTray;
        CloseExitRadio.IsChecked = !_settings.MinimizeToTray;
    }

    // ---------- 开机自启 ----------

    void SetAutoStart(bool on)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
            if (on)
                key.SetValue(RunValueName, $"\"{Environment.ProcessPath}\" --minimized");
            else
                key.DeleteValue(RunValueName, false);
        }
        catch
        {
            _tray?.ShowBalloonTip(2500, Loc.Get("SettingsTitle"), Loc.Get("ImportFailed"),
                System.Windows.Forms.ToolTipIcon.Warning);
        }
    }

    // ---------- 打开位置 ----------

    bool _posComboReady;

    void RebuildOpenPosItems()
    {
        OpenPosCombo.Items.Clear();
        OpenPosCombo.Items.Add(new ComboBoxItem { Content = Loc.Get("OpenPosRemember"), Tag = "remember" });
        OpenPosCombo.Items.Add(new ComboBoxItem { Content = Loc.Get("OpenPosScreen"), Tag = "screen" });
        OpenPosCombo.Items.Add(new ComboBoxItem { Content = Loc.Get("OpenPosNear"), Tag = "near" });
        SyncOpenPos();
    }

    void SyncOpenPos()
    {
        var v = _settings.OpenPosition;
        int idx = v == "screen" ? 1 : v == "near" ? 2 : 0;
        if (OpenPosCombo.SelectedIndex != idx) OpenPosCombo.SelectedIndex = idx;
    }

    void OpenPosCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_posComboReady || OpenPosCombo.SelectedItem is not ComboBoxItem it) return;
        var v = it.Tag as string ?? "remember";
        if (_settings.OpenPosition == v) return;
        _settings.OpenPosition = v;
        _settings.Save();
    }

    // ---------- 语言 ----------

    void ApplyLanguage()
    {
        Title = Loc.Get("AppTitle");
        AppTitleTb.Text = Title;
        HeaderTitleTb.Text = Loc.Get("HeaderTitle");
        InputHintTb.Text = Loc.Get("InputPlaceholder");
        NewDetailsHint.Text = Loc.Get("NewDetailsPlaceholder");
        AddBtn.Content = Loc.Get("Add");
        DetailsToggleBtn.Content = Loc.Get(NewDetailsPanel.Visibility == Visibility.Visible ? "Collapse" : "More");
        PriorityBtn.ToolTip = Loc.Get("PriorityTooltip");
        PriorityLabel.Text = Loc.PriorityName(_newPriority);

        // 创建卡
        NewDetailsLabelTb.Text = Loc.Get("DetailsLabel");
        NewInsertImageBtn.Content = Loc.Get("InsertImage");
        NewPreviewBtn.Content = Loc.Get(_newPreviewOn ? "Edit" : "Preview");
        NewDueLabelTb.Text = Loc.Get("DueLabel");
        NewNoDueBtn.Content = Loc.Get("NoDeadline");
        NewRemindTextTb.Text = Loc.Get("RemindText");
        NewPriorityLabelTb.Text = Loc.Get("PriorityLabel");
        NewP0.Content = Loc.PriorityName(0);
        NewP1.Content = Loc.PriorityName(1);
        NewP2.Content = Loc.PriorityName(2);
        NewP3.Content = Loc.PriorityName(3);
        NewSubHintTb.Text = Loc.Get("AddSubtaskPlaceholder");
        UpdateNewSubsLabel();
        EmptyTitleTb.Text = Loc.Get("EmptyTitle");
        EmptyHintTb.Text = Loc.Get("EmptyHint");
        EmptyHint2Tb.Text = Loc.Get("EmptyHint2");
        ClearDoneBtn.Content = Loc.Get("ClearDone");
        MinBtn.ToolTip = Loc.Get("Minimize");
        BannerTitleTb.Text = Loc.Get("ReminderBannerTitle");
        BannerViewBtn.Content = Loc.Get("View");
        SettingsBtn.ToolTip = Loc.Get("SettingsTip");

        // 详情浮层
        DetailTitleTb.Text = Loc.Get("DetailTitle");
        DetailsLabelTb.Text = Loc.Get("DetailsLabel");
        InsertImageBtn.Content = Loc.Get("InsertImage");
        ImportMdBtn.Content = Loc.Get("ImportMd");
        PreviewBtn.Content = Loc.Get(_notesPreviewOn ? "Edit" : "Preview");
        DueLabelTb.Text = Loc.Get("DueLabel");
        NoDeadlineBtn.Content = Loc.Get("NoDeadline");
        RemindLabelTb.Text = Loc.Get("RemindLabel");
        RemindTextTb.Text = Loc.Get("RemindText");
        RemindHintTb.Text = Loc.Get("RemindHint");
        PriorityLabelTb.Text = Loc.Get("PriorityLabel");
        DetailP0.Content = Loc.PriorityName(0);
        DetailP1.Content = Loc.PriorityName(1);
        DetailP2.Content = Loc.PriorityName(2);
        DetailP3.Content = Loc.PriorityName(3);
        SubtasksLabelTb.Text = Loc.Get("SubtasksLabel");
        AddSubHintTb.Text = Loc.Get("AddSubtaskPlaceholder");
        DetailDeleteBtn.Content = Loc.Get("Delete");
        DetailDoneBtn.Content = Loc.Get("Done");

        // 设置浮层
        SettingsTitleTb.Text = Loc.Get("SettingsTitle");
        LangLabelTb.Text = Loc.Get("LangLabel");
        ThemeLabelTb.Text = Loc.Get("ThemeLabel");
        SetThemeLight.Content = Loc.Get("ThemeLight");
        SetThemeDark.Content = Loc.Get("ThemeDark");
        CloseBehaviorLabelTb.Text = Loc.Get("CloseBehaviorLabel");
        CloseTrayRadio.Content = Loc.Get("MinimizeToTray");
        CloseExitRadio.Content = Loc.Get("ExitDirectly");
        AutoStartLabelTb.Text = Loc.Get("AutoStartLabel");
        AutoStartHintTb.Text = Loc.Get("AutoStartHint");
        OpenPosLabelTb.Text = Loc.Get("OpenPosLabel");
        RebuildOpenPosItems();
        _posComboReady = true;
        HotkeyLabelTb.Text = Loc.Get("HotkeyLabel");
        HkMain.RefreshLocale();
        HkTodo.RefreshLocale();
        HkMd.RefreshLocale();
        HkLaunch.RefreshLocale();
        MdAssocLabelTb.Text = Loc.Get("MdAssocLabel");
        MdAssocToggleTb.Text = Loc.Get("MdAssocToggle");
        SettingsAboutTb.Text = Loc.F("AboutFmt", AppVersion);

        // 日期行 + 日期控件语言
        if (Loc.Lang == "en")
        {
            DateText.Text = DateTime.Now.ToString("dddd, MMM d", System.Globalization.CultureInfo.InvariantCulture);
            var enUs = System.Windows.Markup.XmlLanguage.GetLanguage("en-US");
            DueDatePicker.Language = enUs;
            NewDatePicker.Language = enUs;
        }
        else
        {
            DateText.Text = $"{DateTime.Now.Month}月{DateTime.Now.Day}日 · {DateTime.Now.ToString("dddd", new System.Globalization.CultureInfo("zh-CN"))}";
            var zhCn = System.Windows.Markup.XmlLanguage.GetLanguage("zh-CN");
            DueDatePicker.Language = zhCn;
            NewDatePicker.Language = zhCn;
        }

        // 托盘菜单文本（重建）
        if (_tray != null)
        {
            _tray.ContextMenuStrip?.Dispose();
            _tray.ContextMenuStrip = BuildTrayMenu();
        }

        LangZh.IsChecked = Loc.Lang == "zh";
        LangEn.IsChecked = Loc.Lang == "en";
        SyncThemeChecks();

        foreach (var vm in _all)
            vm.RefreshLocale();
        LaunchView.RefreshLocale();

        UpdateCloseTooltip();
        UpdateHotkeyPill();
        UpdateStats();
        // 预览开着时切语言：用新语言重渲染
        if (_detailVm != null)
        {
            DetailMetaText.Text = Loc.F("CreatedAtFmt", _detailVm.CreatedText);
            SubtasksLabelTb.Text = $"{Loc.Get("SubtasksLabel")} {_detailVm.Children.Count}";
            BreadcrumbTb.Text = _detailVm.Parent == null ? "" : Loc.F("BreadcrumbFmt", _detailVm.Breadcrumb);
            if (_notesPreviewOn)
            {
                MarkdownLite.Render(NotesPreviewPanel, _detailVm.Details, name => Attachments.GetPath(_detailVm.Id, name));
            }
        }
    }

    // ---------- 设置浮层 ----------

    void SettingsBtn_Click(object sender, RoutedEventArgs e) => ShowSettings();

    void ShowSettings()
    {
        LangZh.IsChecked = Loc.Lang == "zh";
        LangEn.IsChecked = Loc.Lang == "en";
        SyncThemeChecks();
        SyncCloseBehavior();
        AutoStartToggle.IsChecked = _settings.AutoStart;
        SyncMdAssoc();
        SyncHotkeyChecks();

        SettingsOverlay.Visibility = Visibility.Visible;
        _settingsShownAt = Environment.TickCount64;
        var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180));
        var scale = new DoubleAnimation(0.96, 1, TimeSpan.FromMilliseconds(220))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        SettingsMask.BeginAnimation(UIElement.OpacityProperty, fade);
        SettingsCard.BeginAnimation(UIElement.OpacityProperty, fade);
        SettingsScale.BeginAnimation(ScaleTransform.ScaleXProperty, scale);
        SettingsScale.BeginAnimation(ScaleTransform.ScaleYProperty, scale);
    }

    void CloseSettings() => SettingsOverlay.Visibility = Visibility.Collapsed;

    void SettingsMask_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (Environment.TickCount64 - _settingsShownAt < 400) return; // 防双击误关
        CloseSettings();
    }

    void SettingsCloseBtn_Click(object sender, RoutedEventArgs e) => CloseSettings();

    void LangZh_Checked(object sender, RoutedEventArgs e) => SwitchLanguage("zh");

    void LangEn_Checked(object sender, RoutedEventArgs e) => SwitchLanguage("en");

    void SetThemeLight_Checked(object sender, RoutedEventArgs e) => SwitchTheme(Theme.Light);

    void SetThemeDark_Checked(object sender, RoutedEventArgs e) => SwitchTheme(Theme.Dark);

    void SwitchTheme(string theme)
    {
        if (_settings.Theme == theme) return;
        _settings.Theme = theme;
        _settings.Save();
        Theme.Apply(theme);
        SyncThemeChecks();
    }

    void SyncThemeChecks()
    {
        if (SetThemeLight == null) return;
        SetThemeLight.IsChecked = _settings.Theme != Theme.Dark;
        SetThemeDark.IsChecked = _settings.Theme == Theme.Dark;
    }

    void SwitchLanguage(string lang)
    {
        if (Loc.Lang == lang) return;
        Loc.SetLang(lang);
        _settings.Language = lang;
        _settings.Save();
        ApplyLanguage();
    }

    void CloseTrayRadio_Checked(object sender, RoutedEventArgs e) => SetCloseBehavior(true);

    void CloseExitRadio_Checked(object sender, RoutedEventArgs e) => SetCloseBehavior(false);

    void SetCloseBehavior(bool minimizeToTray)
    {
        if (_settings.MinimizeToTray == minimizeToTray) return;
        _settings.MinimizeToTray = minimizeToTray;
        _settings.Save();
        UpdateCloseTooltip();
        if (_trayToggleItem != null) _trayToggleItem.Checked = minimizeToTray;
    }

    void AutoStartToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (AutoStartToggle.IsChecked == null) return;
        var on = AutoStartToggle.IsChecked == true;
        if (_settings.AutoStart == on) return;
        _settings.AutoStart = on;
        _settings.Save();
        SetAutoStart(on);
    }

    // ---------- MD 文件关联 ----------

    void MdAssocToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (MdAssocToggle.IsChecked == null) return;
        var on = MdAssocToggle.IsChecked == true;
        if (_settings.MdAssociate == on) return;
        _settings.MdAssociate = on;
        _settings.Save();
        ApplyMdAssociation(on);
    }

    void ApplyMdAssociation(bool on)
    {
        try
        {
            var exe = Environment.ProcessPath;
            using (var pid = Registry.CurrentUser.CreateSubKey(@"Software\Classes\JeffBox.Markdown"))
            {
                pid.SetValue("", "Toolbox Markdown Document");
                pid.CreateSubKey("shell\\open\\command").SetValue("", "\"" + exe + "\" \"%1\"");
                pid.CreateSubKey("DefaultIcon").SetValue("", "\"" + exe + "\"");
            }
            if (on)
            {
                foreach (var extName in new[] { ".md", ".markdown" })
                {
                    using var ext = Registry.CurrentUser.CreateSubKey("Software\\Classes\\" + extName);
                    ext.SetValue("", "JeffBox.Markdown");
                    using var ow = ext.CreateSubKey("OpenWithProgids");
                    ow.SetValue("JeffBox.Markdown", string.Empty);
                    try
                    {
                        Registry.CurrentUser.DeleteSubKeyTree(
                            "Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\FileExts\\" + extName + "\\UserChoice",
                            false);
                    }
                    catch { }
                }
            }
            else
            {
                foreach (var extName in new[] { ".md", ".markdown" })
                {
                    using var ext = Registry.CurrentUser.OpenSubKey("Software\\Classes\\" + extName, true);
                    if (ext?.GetValue("") as string == "JeffBox.Markdown") ext.SetValue("", "");
                }
            }
        }
        catch
        {
            ShowTrayWarning(Loc.Get("ImportFailed"));
        }
    }

    void SyncMdAssoc()
    {
        if (MdAssocToggle == null) return;
        MdAssocToggle.IsChecked = _settings.MdAssociate;
    }

    // ---------- 窗口按钮 ----------

    void MinBtn_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    void MaxBtn_Click(object sender, RoutedEventArgs e) => ToggleMaximize();

    void ToggleMaximize() =>
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        // 最大化 ⇄ 还原时切换按钮图标（E922 □ / E923 ❐）
        if (MaxBtnIcon != null)
            MaxBtnIcon.Text = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
        FixMaximizedMargin();
    }

    /// <summary>
    /// WPF 最大化时窗口 HWND 比屏幕四边各大出一圈边框（SM_CXSIZEFRAME+SM_CXPADDEDBORDER），
    /// 内容从 HWND(0,0) 绘制，屏幕外的 8px 会被裁掉。按系统指标给根布局加补偿边距。
    /// </summary>
    void FixMaximizedMargin()
    {
        if (RootGrid == null) return;
        if (WindowState != WindowState.Maximized)
        {
            RootGrid.Margin = new Thickness(0);
            return;
        }
        var dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var frameX = (Native.GetSystemMetrics(32) + Native.GetSystemMetrics(92)) / dpi; // SM_CXSIZEFRAME + SM_CXPADDEDBORDER
        var frameY = (Native.GetSystemMetrics(33) + Native.GetSystemMetrics(92)) / dpi; // SM_CYSIZEFRAME + SM_CXPADDEDBORDER
        RootGrid.Margin = new Thickness(frameX, frameY, frameX, frameY);
    }

    // ---------- 工具切换 ----------

    void NavTodo_Checked(object sender, RoutedEventArgs e) => SwitchTool(ToolTab.Todo);

    void NavMd_Checked(object sender, RoutedEventArgs e) => SwitchTool(ToolTab.Md);

    void NavLaunch_Checked(object sender, RoutedEventArgs e) => SwitchTool(ToolTab.Launch);

    void SwitchTool(ToolTab tab)
    {
        if (_toolViews == null || tab == _tab) return;
        _tab = tab;
        // 同步左侧导航选中态：程序化切换（打开MD/热键直达/上次工具恢复）不会自动勾选，
        // 不同步会导致导航高亮停留在待办、内容却是其他工具页
        var nav = tab == ToolTab.Md ? NavMd : tab == ToolTab.Launch ? NavLaunch : NavTodo;
        if (nav.IsChecked != true)
            nav.IsChecked = true; // 触发 NavXxx_Checked → SwitchTool，此时 _tab 已相等会直接返回
        _settings.LastTool = tab == ToolTab.Md ? "md" : tab == ToolTab.Launch ? "launch" : "todo";
        _settings.Save();
        if (tab != ToolTab.Md)
            Title = Loc.Get("AppTitle"); // MD 页会带文件名，切走时复位
        foreach (var kv in _toolViews)
            kv.Value.Visibility = kv.Key == tab ? Visibility.Visible : Visibility.Collapsed;

        if (tab == ToolTab.Md)
        {
            MdView.OnShown();
            MdView.FocusEditor();
        }
    }

    // ---------- 窗口级拖拽：md 文件直接丢进窗口 = 笔记工具打开 ----------

    void Window_DragOver(object sender, DragEventArgs e) => e.Handled = true;

    void Window_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop)) return;
        if (e.Data.GetData(System.Windows.DataFormats.FileDrop) is not string[] files || files.Length == 0) return;
        var md = Array.Find(files, f =>
        {
            var ext = System.IO.Path.GetExtension(f).ToLowerInvariant();
            return ext is ".md" or ".markdown" or ".txt";
        });
        if (md == null) return;
        e.Handled = true;
        SwitchTool(ToolTab.Md);
        MdView.OpenExternal(md);
    }

    // ---------- 窗口状态持久化 ----------

    /// <summary>
    /// 按设置把窗口挪到鼠标处：screen=鼠标所在显示器工作区居中，near=以鼠标为中心。
    /// 全程用物理像素（混合 DPI 多显示器下 SetWindowPos 比换算 DIP 更稳），
    /// 最后夹回工作区，保证窗口完整可见、不压任务栏。
    /// </summary>
    void ApplyOpenPosition()
    {
        var mode = _settings.OpenPosition;
        if (mode != "screen" && mode != "near") return;
        if (_hwnd == IntPtr.Zero) return;
        try
        {
            if (!Native.GetCursorPos(out var pt)) return;
            var mon = Native.MonitorFromPoint(pt, Native.MONITOR_DEFAULTTONEAREST);
            var mi = new Native.MONITORINFO
            {
                cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Native.MONITORINFO>(),
            };
            if (!Native.GetMonitorInfo(mon, ref mi)) return;
            var work = mi.rcWork;
            int waW = work.right - work.left, waH = work.bottom - work.top;
            if (waW <= 0 || waH <= 0) return;

            // 记住的是最大化：先还原、挪屏、再最大化（最大化会落在窗口所在的那块屏）
            bool wasMax = WindowState == WindowState.Maximized;
            if (wasMax) WindowState = WindowState.Normal;

            if (!Native.GetWindowRect(_hwnd, out var wr)) return;
            int w = Math.Min(wr.right - wr.left, waW);
            int h = Math.Min(wr.bottom - wr.top, waH);
            int x = mode == "near" ? pt.x - w / 2 : work.left + (waW - w) / 2;
            int y = mode == "near" ? pt.y - h / 2 : work.top + (waH - h) / 2;
            x = Math.Clamp(x, work.left, work.right - w);
            y = Math.Clamp(y, work.top, work.bottom - h);
            Native.SetWindowPos(_hwnd, IntPtr.Zero, x, y, 0, 0,
                Native.SWP_NOZORDER | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);

            if (wasMax) WindowState = WindowState.Maximized;
        }
        catch { }
    }

    void RestoreWindowBounds()
    {
        if (!double.IsNaN(_settings.WinX) && !double.IsNaN(_settings.WinY) &&
            !double.IsNaN(_settings.WinW) && !double.IsNaN(_settings.WinH) &&
            _settings.WinW >= MinWidth && _settings.WinH >= MinHeight)
        {
            // 越界保护：恢复的位置必须与虚拟屏幕有交集（拔掉显示器后不丢窗口）
            var rect = new Rect(_settings.WinX, _settings.WinY, _settings.WinW, _settings.WinH);
            var screen = new Rect(
                SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
            if (screen.IntersectsWith(rect))
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = _settings.WinX;
                Top = _settings.WinY;
                Width = _settings.WinW;
                Height = _settings.WinH;
            }
        }
        if (_settings.WinMax)
            WindowState = WindowState.Maximized;
    }

    void SaveWindowBounds()
    {
        try
        {
            // 最大化的窗口用 RestoreBounds 记住还原尺寸
            var b = WindowState == WindowState.Maximized ? RestoreBounds : new Rect(Left, Top, Width, Height);
            if (!double.IsNaN(b.X) && !double.IsNaN(b.Y) && b.Width > 0)
            {
                _settings.WinX = b.X; _settings.WinY = b.Y;
                _settings.WinW = b.Width; _settings.WinH = b.Height;
                _settings.WinMax = WindowState == WindowState.Maximized;
                _settings.Save();
            }
        }
        catch { }
    }

    /// <summary>工具页用的托盘气泡警告</summary>
    public void ShowTrayWarning(string msg)
    {
        _tray?.ShowBalloonTip(4000, Loc.Get("AppTitle"), msg, System.Windows.Forms.ToolTipIcon.Warning);
    }

    /// <summary>消费"打开方式"请求：切到笔记工具并加载该文件</summary>
    public void ConsumeOpenRequest()
    {
        try
        {
            var reqPath = Path.Combine(AppPaths.DataDir, "open_request.txt");
            if (!System.IO.File.Exists(reqPath)) return;
            var mdPath = System.IO.File.ReadAllText(reqPath).Trim();
            System.IO.File.Delete(reqPath);
            if (mdPath.Length == 0 || !System.IO.File.Exists(mdPath)) return;
            SwitchTool(ToolTab.Md);
            MdView.OpenExternal(mdPath);
        }
        catch { }
    }

    void CloseBtn_Click(object sender, RoutedEventArgs e) => Close();
}

static class Native
{
    public const uint MOD_ALT = 0x1;
    public const uint MOD_CONTROL = 0x2;
    public const uint MOD_SHIFT = 0x4;
    public const uint MOD_WIN = 0x8;
    public const uint MOD_NOREPEAT = 0x4000;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    // 低级键盘钩子：热键设置捕获用。被其他程序注册为全局热键的组合，
    // 按键会被系统直接拦截、普通 KeyDown 收不到，必须在 LL 层捕获。
    public const int WH_KEYBOARD_LL = 13;
    public const int WM_KEYDOWN = 0x100, WM_KEYUP = 0x101, WM_SYSKEYDOWN = 0x104, WM_SYSKEYUP = 0x105;

    public delegate IntPtr LowLevelKeyboardProc(int code, IntPtr wParam, IntPtr lParam);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    public static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn,
        IntPtr hMod, uint dwThreadId);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    public static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    public static extern short GetAsyncKeyState(int vKey);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, ref uint lpdwProcessId);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    public static extern IntPtr GetModuleHandle(string? moduleName);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    public static extern int GetSystemMetrics(int nIndex);

    // 打开位置：鼠标 / 显示器查询（物理像素）
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    public struct POINT { public int x, y; }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    public struct RECT { public int left, top, right, bottom; }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    public struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    public const uint MONITOR_DEFAULTTONEAREST = 2;
    public const uint SWP_NOSIZE = 0x1, SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    public static extern bool GetCursorPos(out POINT lpPoint);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    public static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int x, int y, int cx, int cy, uint uFlags);
}
