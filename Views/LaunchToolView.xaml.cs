using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using TodoApp.Services;

namespace TodoApp.Views;
using UserControl = System.Windows.Controls.UserControl;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MessageBox = System.Windows.MessageBox;
using DragEventArgs = System.Windows.DragEventArgs;
using DataFormats = System.Windows.DataFormats;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Point = System.Windows.Point;
using DragDropEffects = System.Windows.DragDropEffects;
using Brushes = System.Windows.Media.Brushes;
using DataObject = System.Windows.DataObject;

/// <summary>
/// 快捷启动：多分类磁贴墙。单击启动，拖动磁贴排序或拖到分类页签移动，
/// 拖入程序 / 快捷方式 / 文件 / 文件夹即可添加。
/// </summary>
public partial class LaunchToolView : UserControl
{
    const string TileDragFmt = "TodoApp_LaunchTile";

    public class TileVm : System.ComponentModel.INotifyPropertyChanged
    {
        public required LaunchItem Item { get; init; }
        public string Name => Item.Name;
        public string Path => Item.Path;
        public ImageSource? Icon { get; set; }

        public string CountTip => Item.LaunchCount > 0
            ? Loc.F("LaunchCountFmt", Item.LaunchCount)
            : Loc.Get("LaunchCountZero");

        public void RefreshTip(string prop = nameof(CountTip)) =>
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(prop));

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    }

    public class CatVm
    {
        public required LaunchCategory Cat { get; init; }
        public string Name => string.IsNullOrEmpty(Cat.Name) ? Loc.Get("CatDefaultName") : Cat.Name;
    }

    readonly ObservableCollection<CatVm> _cats = new();
    readonly ObservableCollection<TileVm> _tiles = new();
    LauncherData _data = new();
    LaunchCategory _curCat = new();
    LaunchItem? _editItem;            // null = 新增
    CatVm? _ctxCat;                   // 右键的分类
    TileVm? _ctxTile;                 // 右键的磁贴
    bool _suppressCatEvent;
    ListBoxItem? _dropTab;            // 拖拽悬停高亮的页签

    // 磁贴拖拽状态
    Point _dragStart;
    TileVm? _dragTile;
    bool _potentialDrag;
    bool _wasDragging;

    public LaunchToolView()
    {
        InitializeComponent();
        Tiles.ItemsSource = _tiles;
        Cats.ItemsSource = _cats;
        LoadData();
    }

    public void OnShown() { /* 数据常驻，无需处理 */ }

    public void RefreshLocale()
    {
        if (_cats.Count == 0) return;
        UpdateSortBtn();
        var selCat = (Cats.SelectedItem as CatVm)?.Cat;
        _cats.Clear();
        foreach (var c in _data.Categories) _cats.Add(new CatVm { Cat = c });
        // 恢复选中而不触发切换保存
        _suppressCatEvent = true;
        Cats.SelectedItem = _cats.FirstOrDefault(c => c.Cat == selCat) ?? _cats.FirstOrDefault();
        _suppressCatEvent = false;
    }

    // ---------- 数据 ----------

    void LoadData()
    {
        _data = LauncherStorage.Load();
        _curCat = _data.Categories[0];
        foreach (var c in _data.Categories) _cats.Add(new CatVm { Cat = c });

        var idx = _data.Categories.FindIndex(c => c.Id == _data.SelectedCategory);
        _suppressCatEvent = true;
        Cats.SelectedIndex = idx >= 0 ? idx : 0;
        _suppressCatEvent = false;
        _curCat = _data.Categories[Math.Max(0, Cats.SelectedIndex)];
        RenderTiles();
        UpdateSortBtn();
    }

    void SaveData()
    {
        _data.SelectedCategory = _curCat.Id;
        LauncherStorage.Save(_data);
    }

    void RenderTiles()
    {
        _tiles.Clear();
        // 最常用排序：次数降序（稳定），并列时保持手动顺序；手动模式按列表原序
        var ordered = _data.SortByCount
            ? _curCat.Items.OrderByDescending(it => it.LaunchCount)
            : _curCat.Items.AsEnumerable();
        foreach (var it in ordered)
        {
            var isDir = Directory.Exists(it.Path);
            _tiles.Add(new TileVm
            {
                Item = it,
                Icon = IconExtract.GetCached(it.Path, isDir),
            });
        }
        EmptyState.Visibility = _tiles.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>排序模式下视觉顺序由次数决定，不能把展示顺序写回手动排序列表</summary>
    bool ManualOrder => !_data.SortByCount;

    void SyncTilesOrderToItems()
    {
        if (!ManualOrder) return;
        _curCat.Items = _tiles.Select(t => t.Item).ToList();
    }

    void UpdateSortBtn() =>
        SortBtn.Content = Loc.Get(_data.SortByCount ? "LaunchSortCount" : "LaunchSortManual");

    void SortBtn_Click(object sender, RoutedEventArgs e)
    {
        _data.SortByCount = !_data.SortByCount;
        UpdateSortBtn();
        SaveData();
        RenderTiles();
    }

    TileVm? Selected => Tiles.SelectedItem as TileVm;

    // ---------- 分类页签 ----------

    void Cats_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressCatEvent) return;
        if (Cats.SelectedItem is not CatVm vm) return;
        _curCat = vm.Cat;
        RenderTiles();
        SaveData();
    }

    void AddCatBtn_Click(object sender, RoutedEventArgs e)
    {
        var name = AskCategoryName(Loc.Get("LaunchNewCat"), "");
        if (name == null) return;
        var cat = new LaunchCategory { Name = name };
        var at = Math.Max(0, _data.Categories.IndexOf(_curCat) + 1);
        _data.Categories.Insert(at, cat);
        _cats.Insert(at, new CatVm { Cat = cat });
        _suppressCatEvent = true;
        Cats.SelectedIndex = at;
        _suppressCatEvent = false;
        _curCat = cat;
        RenderTiles();
        SaveData();
    }

    string? AskCategoryName(string title, string initial)
    {
        var owner = Window.GetWindow(this);
        if (owner == null) return null;
        while (true)
        {
            var dlg = new InputDialog(owner, title, Loc.Get("CatNamePlaceholder"), initial);
            if (dlg.ShowDialog() != true) return null;
            if (dlg.InputValue.Length > 0) return dlg.InputValue;
        }
    }

    void Cats_PreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        _ctxCat = HitCat(e.OriginalSource) ?? Cats.SelectedItem as CatVm;
    }

    CatVm? HitCat(object src)
    {
        var d = src as DependencyObject;
        while (d != null && d != Cats)
        {
            if (d is ListBoxItem lbi && lbi.Content is CatVm vm) return vm;
            d = VisualTreeHelper.GetParent(d);
        }
        return null;
    }

    void CatRename_Click(object sender, RoutedEventArgs e)
    {
        if (_ctxCat == null) return;
        var name = AskCategoryName(Loc.Get("LaunchRenameCat"),
            string.IsNullOrEmpty(_ctxCat.Cat.Name) ? Loc.Get("CatDefaultName") : _ctxCat.Cat.Name);
        if (name == null) return;
        _ctxCat.Cat.Name = name;
        RefreshLocale();
        SaveData();
    }

    void CatDelete_Click(object sender, RoutedEventArgs e)
    {
        var vm = _ctxCat;
        if (vm == null) return;
        if (_data.Categories.Count <= 1)
        {
            Warn(Loc.Get("LaunchLastCat"));
            return;
        }
        if (vm.Cat.Items.Count > 0)
        {
            var r = MessageBox.Show(Window.GetWindow(this),
                Loc.F("ConfirmDeleteCatFmt", vm.Name, vm.Cat.Items.Count), Loc.Get("AppTitle"),
                MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (r != MessageBoxResult.Yes) return;
        }
        var idx = _data.Categories.IndexOf(vm.Cat);
        _data.Categories.RemoveAt(idx);
        _cats.RemoveAt(idx);
        if (vm.Cat == _curCat)
        {
            var next = Math.Min(idx, _data.Categories.Count - 1);
            _suppressCatEvent = true;
            Cats.SelectedIndex = next;
            _suppressCatEvent = false;
            _curCat = _data.Categories[next];
            RenderTiles();
        }
        SaveData();
    }

    void CatMoveUp_Click(object sender, RoutedEventArgs e) => MoveCat(_ctxCat, -1);
    void CatMoveDown_Click(object sender, RoutedEventArgs e) => MoveCat(_ctxCat, +1);

    void MoveCat(CatVm? vm, int delta)
    {
        if (vm == null) return;
        var idx = _data.Categories.IndexOf(vm.Cat);
        var to = idx + delta;
        if (to < 0 || to >= _data.Categories.Count) return;
        (_data.Categories[idx], _data.Categories[to]) = (_data.Categories[to], _data.Categories[idx]);
        (_cats[idx], _cats[to]) = (_cats[to], _cats[idx]);
        _suppressCatEvent = true;
        var sel = Cats.SelectedItem;
        Cats.SelectedItem = null;
        Cats.SelectedItem = sel;
        _suppressCatEvent = false;
        SaveData();
    }

    // 磁贴拖到页签上 → 移动到该分类；拖文件到页签 → 加进该分类
    void CatTab_DragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(TileDragFmt) || e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.Move;
            e.Handled = true;
            var lbi = HitCatItem(e.OriginalSource);
            if (!ReferenceEquals(lbi, _dropTab))
            {
                ClearTabHighlight();
                _dropTab = lbi;
                if (_dropTab != null)
                {
                    if (_dropTab.Template.FindName("Bd", _dropTab) is Border bd)
                        bd.Background = Theme.Brush("Accent");
                    if (_dropTab.Template.FindName("Txt", _dropTab) is TextBlock txt)
                        txt.Foreground = Brushes.White;
                }
            }
        }
        else e.Effects = DragDropEffects.None;
    }

    ListBoxItem? HitCatItem(object src)
    {
        var d = src as DependencyObject;
        while (d != null && d != Cats)
        {
            if (d is ListBoxItem lbi) return lbi;
            d = VisualTreeHelper.GetParent(d);
        }
        return null;
    }

    void ClearTabHighlight()
    {
        if (_dropTab != null)
        {
            if (_dropTab.Template.FindName("Bd", _dropTab) is Border bd)
                bd.SetValue(Border.BackgroundProperty, DependencyProperty.UnsetValue);
            if (_dropTab.Template.FindName("Txt", _dropTab) is TextBlock txt)
                txt.SetValue(TextBlock.ForegroundProperty, DependencyProperty.UnsetValue);
        }
        _dropTab = null;
    }

    void CatTab_DragLeave(object sender, DragEventArgs e) => ClearTabHighlight();

    void CatTab_Drop(object sender, DragEventArgs e)
    {
        ClearTabHighlight();
        var target = HitCat(e.OriginalSource)?.Cat ?? _curCat;
        if (e.Data.GetDataPresent(TileDragFmt))
        {
            e.Handled = true;
            var vm = _dragTile;
            if (vm == null || vm.Item.Path == "") return;
            if (target == _curCat) return; // 原分类，无需处理
            _data.Categories.ForEach(c => c.Items.RemoveAll(i => ReferenceEquals(i, vm.Item)));
            target.Items.Add(vm.Item);
            RenderTiles();
            SaveData();
        }
        else if (e.Data.GetDataPresent(DataFormats.FileDrop) &&
                 e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
        {
            e.Handled = true;
            AddDroppedFiles(files, target);
            if (target == _curCat) RenderTiles();
            SaveData();
        }
    }

    // ---------- 增删改 ----------

    void AddBtn_Click(object sender, RoutedEventArgs e)
    {
        _editItem = null;
        NameBox.Text = "";
        PathBox.Text = "";
        OpenEditPanel();
    }

    void EditBtn_Click(object sender, RoutedEventArgs e)
    {
        var sel = _ctxTile ?? Selected;
        if (sel == null) return;
        StartEdit(sel);
    }

    void StartEdit(TileVm vm)
    {
        _editItem = vm.Item;
        NameBox.Text = vm.Item.Name;
        PathBox.Text = vm.Item.Path;
        Tiles.SelectedItem = vm;
        OpenEditPanel();
    }

    void DeleteBtn_Click(object sender, RoutedEventArgs e)
    {
        var sel = _ctxTile ?? Selected;
        if (sel == null) return;
        var r = MessageBox.Show(Window.GetWindow(this),
            Loc.F("ConfirmDelete", sel.Item.Name), Loc.Get("AppTitle"),
            MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (r != MessageBoxResult.Yes) return;
        _curCat.Items.Remove(sel.Item);
        SaveData();
        RenderTiles();
    }

    void OpenEditPanel() => EditPanel.Visibility = Visibility.Visible;

    void CloseEditPanel() => EditPanel.Visibility = Visibility.Collapsed;

    void EditOkBtn_Click(object sender, RoutedEventArgs e)
    {
        var path = PathBox.Text.Trim();
        var name = NameBox.Text.Trim();
        if (path.Length == 0) return;
        if (!Directory.Exists(path) && !File.Exists(path))
        {
            Warn(Loc.Get("ImportFailed") + " · " + path);
            return;
        }
        if (name.Length == 0)
            name = Path.GetFileNameWithoutExtension(path.TrimEnd('\\', '/'));
        if (_editItem != null)
        {
            _editItem.Name = name;
            _editItem.Path = path;
        }
        else
        {
            var item = new LaunchItem { Name = name, Path = path };
            _curCat.Items.Add(item);
        }
        SaveData();
        RenderTiles();
        CloseEditPanel();
    }

    void EditCancelBtn_Click(object sender, RoutedEventArgs e) => CloseEditPanel();

    void BrowseAppBtn_Click(object sender, RoutedEventArgs e)
    {
        var ofd = new Microsoft.Win32.OpenFileDialog
        {
            Filter = Loc.Get("FilterApp"),
            Title = Loc.Get("LaunchPickApp"),
            CheckFileExists = false,
        };
        if (ofd.ShowDialog(Window.GetWindow(this)) != true) return;
        PathBox.Text = ofd.FileName;
        if (NameBox.Text.Trim().Length == 0)
            NameBox.Text = Path.GetFileNameWithoutExtension(ofd.FileName);
    }

    void BrowseFolderBtn_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = Loc.Get("LaunchBrowse") };
        if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;
        PathBox.Text = dlg.FolderName;
        if (NameBox.Text.Trim().Length == 0)
            NameBox.Text = Path.GetFileName(dlg.FolderName.TrimEnd('\\', '/'));
    }

    // ---------- 磁贴右键菜单 ----------

    void Tiles_PreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        var vm = HitTile(e.OriginalSource, out var lbi);
        if (vm == null) return;
        if (lbi != null) lbi.IsSelected = true;
        _ctxTile = vm;
    }

    void TileMenu_Opened(object sender, RoutedEventArgs e)
    {
        var vm = _ctxTile ?? Selected;
        TileMoveMenu.Items.Clear();
        if (vm == null) return;
        var others = _data.Categories.Where(c => c != _curCat).ToList();
        if (others.Count == 0)
        {
            var none = new MenuItem { Header = Loc.Get("LaunchNoOtherCat"), IsEnabled = false };
            TileMoveMenu.Items.Add(none);
            return;
        }
        foreach (var cat in others)
        {
            var target = cat; // 闭包捕获
            var label = string.IsNullOrEmpty(cat.Name) ? Loc.Get("CatDefaultName") : cat.Name;
            var mi = new MenuItem { Header = label };
            mi.Click += (_, _) => MoveTileTo(vm, target);
            TileMoveMenu.Items.Add(mi);
        }
    }

    void MoveTileTo(TileVm vm, LaunchCategory target)
    {
        _curCat.Items.Remove(vm.Item);
        target.Items.Add(vm.Item);
        SaveData();
        RenderTiles();
    }

    void TileOpen_Click(object sender, RoutedEventArgs e)
    {
        var vm = _ctxTile ?? Selected;
        if (vm != null) Launch(vm);
    }

    void TileEdit_Click(object sender, RoutedEventArgs e)
    {
        var vm = _ctxTile ?? Selected;
        if (vm != null) StartEdit(vm);
    }

    void TileDelete_Click(object sender, RoutedEventArgs e) => DeleteBtn_Click(sender, e);

    void TileMoveUp_Click(object sender, RoutedEventArgs e) => MoveTile(_ctxTile ?? Selected, -1);
    void TileMoveDown_Click(object sender, RoutedEventArgs e) => MoveTile(_ctxTile ?? Selected, +1);

    void MoveTile(TileVm? vm, int delta)
    {
        if (vm == null || !ManualOrder) return;
        var idx = _tiles.IndexOf(vm);
        var to = idx + delta;
        if (idx < 0 || to < 0 || to >= _tiles.Count) return;
        _tiles.Move(idx, to);
        SyncTilesOrderToItems();
        SaveData();
    }

    // ---------- 单击启动 / 拖动排序 ----------

    void Tiles_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _wasDragging = false;
        _potentialDrag = false;
        var vm = HitTile(e.OriginalSource, out _);
        if (vm == null) return;
        _dragTile = vm;
        _dragStart = e.GetPosition(Tiles);
        _potentialDrag = true;
    }

    void Tiles_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_potentialDrag || e.LeftButton != MouseButtonState.Pressed || _dragTile == null) return;
        var pos = e.GetPosition(Tiles);
        if (Math.Abs(pos.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(pos.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        _potentialDrag = false;
        _wasDragging = true;
        var tile = _dragTile;
        var data = new DataObject();
        data.SetData(TileDragFmt, tile);
        try
        {
            DragDrop.DoDragDrop(Tiles, data, DragDropEffects.Move);
        }
        finally
        {
            _dragTile = null;
            // 拖到页签的场景已在 CatTab_Drop 处理；这里统一把当前磁贴顺序落盘
            SyncTilesOrderToItems();
            SaveData();
        }
    }

    void Tiles_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_wasDragging) { _wasDragging = false; return; } // 刚完成拖动，不当作单击
        var vm = HitTile(e.OriginalSource, out var lbi);
        if (vm == null) return;
        if (lbi != null) lbi.IsSelected = true;
        Launch(vm);
    }

    TileVm? HitTile(object src, out ListBoxItem? lbi)
    {
        var d = src as DependencyObject;
        while (d != null && d != Tiles)
        {
            if (d is ListBoxItem item)
            {
                lbi = item;
                return Tiles.ItemContainerGenerator.ItemFromContainer(item) as TileVm;
            }
            d = VisualTreeHelper.GetParent(d);
        }
        lbi = null;
        return null;
    }

    void Tiles_DragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(TileDragFmt))
        {
            e.Effects = DragDropEffects.Move;
            e.Handled = true;

            // 拖到边缘时自动滚动
            var sp = e.GetPosition(TileScroller);
            if (sp.Y <= 24) TileScroller.ScrollToVerticalOffset(TileScroller.VerticalOffset - 20);
            else if (sp.Y >= TileScroller.ActualHeight - 24)
                TileScroller.ScrollToVerticalOffset(TileScroller.VerticalOffset + 20);

            var vm = _dragTile;
            if (vm == null) return;
            var idx = TileInsertIndexFromPoint(e.GetPosition(Tiles));
            if (idx < 0) return;
            var from = _tiles.IndexOf(vm);
            if (!ManualOrder) return; // 次数排序模式下顺序自动决定，不做拖动重排
            // idx 是"插入位置"(0..Count)；Move 需要移除后的目标下标，右移要 -1，
            // 否则 Move 先 Remove 再 Insert 越界，条目会从集合中丢失
            var to = idx > from ? idx - 1 : idx;
            if (from >= 0 && to >= 0 && to < _tiles.Count && to != from)
            {
                try
                {
                    _tiles.Move(from, to);
                    Tiles.UpdateLayout(); // 让后续命中测试用新布局
                }
                catch
                {
                    if (!_tiles.Contains(vm))
                        _tiles.Insert(Math.Clamp(to, 0, _tiles.Count), vm); // 兜底防丢失
                }
            }
        }
        else if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.Copy;
            e.Handled = true;
        }
        else e.Effects = DragDropEffects.None;
    }

    /// <summary>光标处应插入的磁贴下标；-1 表示位置无效不移动</summary>
    int TileInsertIndexFromPoint(Point p)
    {
        var lastBottom = 0.0;
        for (var i = 0; i < _tiles.Count; i++)
        {
            if (Tiles.ItemContainerGenerator.ContainerFromIndex(i) is not ListBoxItem lbi) continue;
            var r = new Rect(lbi.TranslatePoint(new Point(), Tiles), lbi.RenderSize);
            lastBottom = Math.Max(lastBottom, r.Bottom);
            if (r.Contains(p)) return p.X <= r.Left + r.Width / 2 ? i : i + 1;
        }
        // 空白区域：低于最后一行才允许追加到末尾
        return p.Y > lastBottom ? _tiles.Count : -1;
    }

    void Tiles_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(TileDragFmt))
        {
            e.Handled = true;
            SyncTilesOrderToItems();
            SaveData();
        }
        else if (e.Data.GetDataPresent(DataFormats.FileDrop) &&
                 e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
        {
            e.Handled = true;
            AddDroppedFiles(files, _curCat);
            RenderTiles();
            SaveData();
        }
    }

    void Tiles_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { e.Handled = true; if (Selected != null) Launch(Selected); }
        else if (e.Key == Key.F2) { e.Handled = true; EditBtn_Click(sender, e); }
        else if (e.Key == Key.Delete) { e.Handled = true; DeleteBtn_Click(sender, e); }
    }

    void Launch(TileVm vm)
    {
        vm.Item.LaunchCount++;
        vm.RefreshTip();
        SaveData();
        if (_data.SortByCount) RenderTiles(); // 次数变化后立即按热度重排
        try
        {
            Process.Start(new ProcessStartInfo(vm.Item.Path) { UseShellExecute = true });
        }
        catch
        {
            Warn(Loc.Get("ImportFailed") + " · " + vm.Item.Path);
        }
    }

    // ---------- 外部拖入添加 ----------

    void View_DragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.Copy;
            e.Handled = true;
        }
        else e.Effects = DragDropEffects.None;
    }

    void View_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files || files.Length == 0) return;
        e.Handled = true;
        AddDroppedFiles(files, _curCat);
        RenderTiles();
        SaveData();
    }

    void AddDroppedFiles(string[] files, LaunchCategory cat)
    {
        foreach (var f in files)
        {
            if (!Directory.Exists(f) && !File.Exists(f)) continue;
            if (cat.Items.Any(i => i.Path.Equals(f, StringComparison.OrdinalIgnoreCase))) continue;
            var trimmed = f.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var name = Path.GetFileNameWithoutExtension(trimmed);
            if (string.IsNullOrEmpty(name)) name = trimmed;
            cat.Items.Add(new LaunchItem { Name = name, Path = f });
        }
    }

    void Warn(string msg)
    {
        var win = Window.GetWindow(this) as MainWindow;
        win?.ShowTrayWarning(msg);
    }
}
