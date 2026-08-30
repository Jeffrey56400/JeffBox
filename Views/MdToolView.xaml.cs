using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using TodoApp.Services;

namespace TodoApp.Views;
using UserControl = System.Windows.Controls.UserControl;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MessageBox = System.Windows.MessageBox;
using DragEventArgs = System.Windows.DragEventArgs;
using DataFormats = System.Windows.DataFormats;
using Button = System.Windows.Controls.Button;

/// <summary>
/// Markdown 编辑阅读器：单页内「编辑 ⇄ 预览」切换。
/// 性能约束：预览只在切入预览态时渲染一次（非边打字边渲染），图片解码限宽且冻结。
/// </summary>
public partial class MdToolView : UserControl
{
    string? _file;
    bool _dirty;
    bool _previewMode;
    bool _loading;
    bool _readOnly;
    bool _fileLocked;      // NTFS 只读属性或大文件：禁止编辑
    bool _userNew;         // 用户主动新建过：切回本页时不自动恢复 RecentMd
    IEnumerator<FrameworkElement>? _blockEnumerator;

    // 超过 2MB 自动切只读阅读；预览分块续载（首屏 300 块，接近底部再续 300）
    const long ReadOnlyBytes = 2 * 1024 * 1024;

    public bool IsDirty => _dirty;

    public void FocusEditor() => Editor.Focus();

    public MdToolView()
    {
        InitializeComponent();
        RecentBtn.Click += RecentBtn_Click;
        UpdatePathLabel();
    }

    /// <summary>外部打开（双击 / 打开方式 / 拖入）：加载后进入阅读（预览）模式</summary>
    public void OpenExternal(string path)
    {
        if (!ConfirmDiscard()) return;
        LoadFile(path);
        if (_readOnly)
            EnterPreviewMode();
        else
            EnterPreviewMode();
    }

    /// <summary>切到本工具页时调用：自动恢复上次编辑的文件</summary>
    public void OnShown()
    {
        if (_file == null && Editor.Text.Length == 0 && !_userNew)
        {
            var last = AppSettings.Load().RecentMd;
            if (!string.IsNullOrEmpty(last) && File.Exists(last))
                LoadFile(last);
        }
    }

    // ---------- 状态 ----------

    void SetDirty(bool dirty)
    {
        _dirty = dirty;
        UpdatePathLabel();
    }

    void UpdatePathLabel()
    {
        var display = _file ?? Loc.Get("MdUntitled");
        if (_readOnly) display += " · " + Loc.Get("MdReadOnlyHint");
        PathLabel.Text = _dirty ? display + " " + Loc.Get("MdDirty") : display;
    }

    void SaveRecent()
    {
        if (_file == null) return;
        var s = AppSettings.Load();
        if (s.RecentMd != _file)
        {
            s.RecentMd = _file;
        }
        // 最近列表：去重置顶，上限 10
        s.RecentFiles.RemoveAll(f => f.Equals(_file, StringComparison.OrdinalIgnoreCase));
        s.RecentFiles.Insert(0, _file);
        if (s.RecentFiles.Count > 10) s.RecentFiles.RemoveRange(10, s.RecentFiles.Count - 10);
        s.Save();
    }

    // ---------- 最近打开 ----------

    void RecentBtn_Click(object sender, RoutedEventArgs e)
    {
        BuildRecentMenu();
        if (RecentBtn.ContextMenu != null)
        {
            RecentBtn.ContextMenu.PlacementTarget = RecentBtn;
            RecentBtn.ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            RecentBtn.ContextMenu.IsOpen = true;
        }
    }

    void BuildRecentMenu()
    {
        var menu = new ContextMenu();
        var files = AppSettings.Load().RecentFiles;
        var shown = 0;
        foreach (var f in files)
        {
            if (!File.Exists(f)) continue;
            shown++;
            var path = f;
            var mi = new MenuItem
            {
                Header = System.IO.Path.GetFileName(path),
                ToolTip = path,
            };
            mi.Click += (_, _) =>
            {
                if (!File.Exists(path))
                {
                    RemoveRecent(path);
                    _trayWarn(Loc.Get("MdFileMissing") + " · " + path);
                    return;
                }
                if (!ConfirmDiscard()) return;
                LoadFile(path);
            };
            menu.Items.Add(mi);
        }
        if (shown == 0)
            menu.Items.Add(new MenuItem
            {
                Header = Loc.Get("MdRecentEmpty"),
                IsEnabled = false,
            });
        RecentBtn.ContextMenu = menu;
    }

    static void RemoveRecent(string path)
    {
        var s = AppSettings.Load();
        s.RecentFiles.RemoveAll(f => f.Equals(path, StringComparison.OrdinalIgnoreCase));
        if (s.RecentMd == path) s.RecentMd = "";
        s.Save();
    }

    // ---------- 文件操作 ----------

    void NewBtn_Click(object sender, RoutedEventArgs e)
    {
        if (!ConfirmDiscard()) return;
        _loading = true;
        _file = null;
        Editor.Clear();
        _loading = false;
        _readOnly = false;          // 新建可编辑（不复位会卡在只读预览）
        _fileLocked = false;
        _userNew = true;
        var st = AppSettings.Load();
        if (st.RecentMd.Length > 0) { st.RecentMd = ""; st.Save(); }
        SetDirty(false);
        UpdatePathLabel();
        if (_previewMode) RenderPreview();
        EnterEditMode();
        Editor.Focus();
    }

    void OpenBtn_Click(object sender, RoutedEventArgs e)
    {
        if (!ConfirmDiscard()) return;
        var ofd = new Microsoft.Win32.OpenFileDialog
        {
            Filter = Loc.Get("FilterMd"),
            Title = Loc.Get("MdOpen"),
        };
        if (ofd.ShowDialog(Window.GetWindow(this)) != true) return;
        LoadFile(ofd.FileName);
    }

    void LoadFile(string path)
    {
        try
        {
            _loading = true;
            Editor.Text = ReadSmart(path);
            _file = path;
            _readOnly = new FileInfo(path).Length > ReadOnlyBytes;
            try
            {
                _fileLocked = _readOnly ||
                    (System.IO.File.GetAttributes(path) & System.IO.FileAttributes.ReadOnly) != 0;
            }
            catch { _fileLocked = _readOnly; }
            SetDirty(false);
            _userNew = false;
            SaveRecent();
            UpdatePathLabel();
            UpdateWindowTitle();
            if (_previewMode || _readOnly) RenderPreview();
            if (_readOnly) EnterPreviewMode();
        }
        catch
        {
            _trayWarn(path);
        }
        finally
        {
            _loading = false;
        }
    }

    /// <summary>智能读取：BOM → 严格 UTF-8 → GB18030（中文 md 常见编码，避免乱码/破坏原文件）</summary>
    static string ReadSmart(string path)
    {
        var bytes = File.ReadAllBytes(path);
        // BOM
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return new UTF8Encoding(false).GetString(bytes, 3, bytes.Length - 3);
        // 严格 UTF-8 校验
        try
        {
            var strict = new UTF8Encoding(false, throwOnInvalidBytes: true);
            return strict.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            try { return Encoding.GetEncoding("GB18030").GetString(bytes); }
            catch { return new UTF8Encoding(false).GetString(bytes); }
        }
    }

    void SaveBtn_Click(object sender, RoutedEventArgs e) => Save();

    void SaveAsBtn_Click(object sender, RoutedEventArgs e) => SaveAs();

    bool Save()
    {
        if (_file == null) return SaveAs();
        return WriteTo(_file);
    }

    bool SaveAs()
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Filter = Loc.Get("FilterMd"),
            FileName = Path.GetFileName(_file ?? Loc.Get("MdUntitled")),
        };
        if (dlg.ShowDialog(Window.GetWindow(this)) != true) return false;
        _file = dlg.FileName;
        var ok = WriteTo(_file);
        if (ok) { SetDirty(false); SaveRecent(); UpdatePathLabel(); }
        return ok;
    }

    bool WriteTo(string path)
    {
        try
        {
            File.WriteAllText(path, Editor.Text, new UTF8Encoding(true)); // BOM：记事本等工具友好
            SetDirty(false);
            _file = path;
            UpdatePathLabel();
            return true;
        }
        catch
        {
            _trayWarn(path);
            return false;
        }
    }

    void _trayWarn(string path)
    {
        var win = Window.GetWindow(this) as MainWindow;
        win?.ShowTrayWarning(Loc.Get("ImportFailed") + " · " + path);
    }

    /// <summary>未保存时询问；返回 false = 用户取消操作</summary>
    public bool ConfirmDiscard()
    {
        if (!_dirty) return true;
        var r = MessageBox.Show(Window.GetWindow(this), Loc.Get("MdDirtyHint"),
            Loc.Get("AppTitle"), MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (r == MessageBoxResult.Cancel) return false;
        if (r == MessageBoxResult.Yes) return Save();
        SetDirty(false);
        return true;
    }

    // ---------- 编辑 ⇄ 预览 ----------

    void ModeBtn_Click(object sender, RoutedEventArgs e) => ToggleMode();

    void ToggleMode()
    {
        if (_readOnly || _fileLocked) return;
        if (!_previewMode)
            EnterPreviewMode();
        else
            EnterEditMode();
    }

    void EnterPreviewMode()
    {
        _previewMode = true;
        Editor.Visibility = Visibility.Collapsed;
        PreviewHost.Visibility = Visibility.Visible;
        ModeBtn.Content = Loc.Get("Edit");
        StartPreviewRender();
    }

    void EnterEditMode()
    {
        if (_readOnly || _fileLocked) { EnterPreviewMode(); return; }
        _previewMode = false;
        PreviewHost.Visibility = Visibility.Collapsed;
        Editor.Visibility = Visibility.Visible;
        ModeBtn.Content = Loc.Get("Preview");
        Editor.Focus();
    }

    void RenderPreview() => StartPreviewRender();

    // ---------- 分块渲染流水线 ----------

    void StartPreviewRender()
    {
        PreviewPanel.Children.Clear();
        _blockEnumerator?.Dispose();
        _blockEnumerator = MarkdownLite.EnumerateBlocks(Editor.Text, ResolveImage).GetEnumerator();
        AppendBlocks(300); // 首屏即显，后续滚动续载
    }

    void AppendBlocks(int count)
    {
        if (_blockEnumerator == null) return;
        int n = 0;
        while (n < count && _blockEnumerator.MoveNext())
        {
            PreviewPanel.Children.Add(_blockEnumerator.Current);
            n++;
        }
        if (n < count)
        {
            _blockEnumerator.Dispose();
            _blockEnumerator = null;
        }
    }

    void PreviewHost_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (_blockEnumerator == null) return;
        if (e.VerticalOffset + e.ViewportHeight >= e.ExtentHeight - 400)
            AppendBlocks(300);
    }

    string? ResolveImage(string name)
    {
        try
        {
            if (Path.IsPathRooted(name))
                return File.Exists(name) ? name : null;
            if (_file == null) return null;
            var dir = Path.GetDirectoryName(_file);
            if (string.IsNullOrEmpty(dir)) return null;
            var p = Path.GetFullPath(Path.Combine(dir, name));
            return File.Exists(p) ? p : null;
        }
        catch
        {
            return null;
        }
    }

    // ---------- 编辑器事件 ----------

    void Editor_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading) return;
        SetDirty(true);
    }

    void Editor_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Control) return;
        if (e.Key == Key.S) { e.Handled = true; Save(); }
        else if (e.Key == Key.E) { e.Handled = true; ToggleMode(); }
        else if (e.Key == Key.O) { e.Handled = true; OpenBtn_Click(sender, e); }
        else if (e.Key == Key.N) { e.Handled = true; NewBtn_Click(sender, e); }
    }

    /// <summary>预览模式下同样可用 Ctrl+O/N（挂在 UserControl 级）</summary>
    void View_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Control) return;
        if (e.Key == Key.O) { e.Handled = true; OpenBtn_Click(sender, e); }
        else if (e.Key == Key.N) { e.Handled = true; NewBtn_Click(sender, e); }
    }

    // ---------- 拖拽打开 ----------
    void View_DragOver(object sender, DragEventArgs e) => e.Handled = true;

    void View_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files || files.Length == 0) return;
        var md = files.FirstOrDefault(f =>
        {
            var ext = System.IO.Path.GetExtension(f).ToLowerInvariant();
            return ext is ".md" or ".markdown" or ".txt";
        });
        if (md == null) return;
        e.Handled = true;
        if (!ConfirmDiscard()) return;
        LoadFile(md);
        EnterPreviewMode();
    }

    void UpdateWindowTitle()
    {
        if (Window.GetWindow(this) is { } w)
            w.Title = (_file == null ? Loc.Get("MdUntitled") : System.IO.Path.GetFileName(_file)) + " · " + Loc.Get("AppTitle");
    }
}
