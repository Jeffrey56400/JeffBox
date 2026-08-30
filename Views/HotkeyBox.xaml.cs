using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using TodoApp.Services;

namespace TodoApp.Views;
using UserControl = System.Windows.Controls.UserControl;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MouseButtonEventArgs = System.Windows.Input.MouseButtonEventArgs;
using Brush = System.Windows.Media.Brush;

/// <summary>
/// 热键捕获框：点选后按下组合键即录入。内建低级键盘钩子——
/// 被其他程序注册为全局热键的组合（普通 KeyDown 收不到）也能捕获；
/// 录入失败时观察 800ms 内的前台变化，给出"谁响应了按键"的实证提示。
/// 注册/持久化由宿主通过 Apply / Disable 事件完成。
/// </summary>
public partial class HotkeyBox : UserControl
{
    /// <summary>宿主尝试启用组合；返回 null 表示成功，否则返回给用户看的失败原因</summary>
    public event Func<string, string?>? Apply;
    /// <summary>宿主停用此槽位</summary>
    public event Action? Disable;
    /// <summary>宿主判断组合是否应被钩子吞掉（如与自己其他槽位重复，避免按键触发那个槽）</summary>
    public Func<string, bool>? ShouldSwallow { get; set; }

    public string Hotkey { get; private set; } = "";

    string _labelKey = "HotkeyMainLabel";

    // 低级键盘钩子
    Native.LowLevelKeyboardProc? _llProc;
    IntPtr _llHook;
    uint _fgPidBefore;      // 按键分发前的前台进程（响应者取证基准）
    bool _comboPending;     // 本次按下已交给宿主处理（失败时启动取证）

    readonly DispatcherTimer _responderTimer = new() { Interval = TimeSpan.FromMilliseconds(800) };
    string? _conflictBase;  // 取证前的冲突文案

    static readonly Brush OkBrush = new SolidColorBrush(
        System.Windows.Media.Color.FromRgb(0x3B, 0xA5, 0x5D));
    static readonly Brush OkBrushFrozen = CreateFrozen(OkBrush);

    static Brush CreateFrozen(Brush b)
    {
        if (b is SolidColorBrush sc) sc.Freeze();
        return b;
    }

    public HotkeyBox()
    {
        InitializeComponent();
        _responderTimer.Tick += (_, _) => CheckResponder();
    }

    public string LabelKey
    {
        get => _labelKey;
        set { _labelKey = value; LabelText.Text = Loc.Get(value); }
    }

    /// <summary>宿主回填：当前组合与注册结果</summary>
    public void SetHotkey(string text, bool registered)
    {
        Hotkey = text;
        _lastRegistered = registered;
        if (!Box.IsKeyboardFocused)
            ComboText.Text = string.IsNullOrEmpty(text) ? Loc.Get("HotkeyCapture") : text;
        if (string.IsNullOrEmpty(text))
        {
            StatusText.Text = Loc.Get("HotkeyOffStatus");
            StatusText.Foreground = Theme.Brush("TextSub");
        }
        else if (registered)
        {
            StatusText.Text = Loc.Get("HotkeyOk");
            StatusText.Foreground = OkBrushFrozen;
        }
        else
        {
            StatusText.Text = HotkeyInfo.ConflictHint(text);
            StatusText.Foreground = Theme.Brush("Danger");
        }
    }

    public void RefreshLocale()
    {
        LabelText.Text = Loc.Get(_labelKey);
        OffBtn.Content = Loc.Get("Off");
        if (!Box.IsKeyboardFocused) SetHotkey(Hotkey, _lastRegistered);
        else StatusText.Text = Loc.Get("HotkeyCancelHint");
    }

    bool _lastRegistered;

    // ---------- 捕获 ----------

    void Box_Click(object sender, MouseButtonEventArgs e) => Box.Focus();

    void Box_FocusChanged(object sender, RoutedEventArgs e)
    {
        var focused = Box.IsKeyboardFocused;
        Box.BorderBrush = focused ? Theme.Brush("Accent") : Theme.Brush("Line");
        if (focused)
        {
            InstallHook();
            ComboText.Text = string.IsNullOrEmpty(Hotkey) ? Loc.Get("HotkeyCapture") : Hotkey;
            StatusText.Text = Loc.Get("HotkeyCancelHint");
            StatusText.Foreground = Theme.Brush("TextSub");
        }
        else
        {
            RemoveHook();
            _responderTimer.Stop();
            ComboText.Text = string.IsNullOrEmpty(Hotkey) ? Loc.Get("HotkeyCapture") : Hotkey;
        }
    }

    // 无钩子时的兜底（正常场景 LL 钩子先处理）
    void Box_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        e.Handled = true;
        if (_llHook != IntPtr.Zero) return;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Escape) { ExitCapture(); return; }
        if (IsModifier(key)) return;
        ApplyCaptured(HotkeyInfo.Format(Keyboard.Modifiers, key), 0);
    }

    static bool IsModifier(Key k) =>
        k is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.System;

    void ExitCapture() => Box.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));

    // ---------- 低级键盘钩子 ----------

    void InstallHook()
    {
        if (_llHook != IntPtr.Zero) return;
        _llProc = HookProc;
        _llHook = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, _llProc,
            Native.GetModuleHandle(null), 0);
    }

    void RemoveHook()
    {
        if (_llHook == IntPtr.Zero) return;
        Native.UnhookWindowsHookEx(_llHook);
        _llHook = IntPtr.Zero;
        _llProc = null;
    }

    IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0 && (wParam == (IntPtr)Native.WM_KEYDOWN || wParam == (IntPtr)Native.WM_SYSKEYDOWN))
        {
            var vk = System.Runtime.InteropServices.Marshal.ReadInt32(lParam);
            var key = KeyInterop.KeyFromVirtualKey(vk);
            var ctrl = (Native.GetAsyncKeyState(0x11) & 0x8000) != 0;
            var alt = (Native.GetAsyncKeyState(0x12) & 0x8000) != 0;
            var shift = (Native.GetAsyncKeyState(0x10) & 0x8000) != 0;
            var win = (Native.GetAsyncKeyState(0x5B) & 0x8000) != 0 ||
                      (Native.GetAsyncKeyState(0x5C) & 0x8000) != 0;
            // 分发前记下前台进程——若随后变化，说明有程序响应了这次按键
            var fg = Native.GetForegroundWindow();
            uint pidBefore = 0;
            if (fg != IntPtr.Zero) Native.GetWindowThreadProcessId(fg, ref pidBefore);

            // Esc 和与自家其他槽位重复的组合吞掉：前者只退出捕获不关浮层，
            // 后者避免按键真的触发那个槽（窗口被切走/收起）
            var swallow = false;
            if (key == Key.Escape) swallow = true;
            else if (!IsModifier(key))
            {
                var mods = ModifierKeys.None;
                if (ctrl) mods |= ModifierKeys.Control;
                if (alt) mods |= ModifierKeys.Alt;
                if (shift) mods |= ModifierKeys.Shift;
                if (win) mods |= ModifierKeys.Windows;
                var text = HotkeyInfo.Format(mods, key);
                if (text != null && ShouldSwallow?.Invoke(text) == true) swallow = true;
            }
            // 外部软件占用的组合不吞：放行分发，才能观察"谁响应了按键"取证
            Dispatcher.BeginInvoke(() => HandleKey(vk, ctrl, alt, shift, win, pidBefore));
            if (swallow) return (IntPtr)1;
        }
        return Native.CallNextHookEx(_llHook, code, wParam, lParam);
    }

    void HandleKey(int vk, bool ctrl, bool alt, bool shift, bool win, uint pidBefore)
    {
        if (_llHook == IntPtr.Zero) return;
        var key = KeyInterop.KeyFromVirtualKey(vk);
        if (key == Key.Escape) { ExitCapture(); return; }
        if (IsModifier(key)) return;

        var mods = ModifierKeys.None;
        if (ctrl) mods |= ModifierKeys.Control;
        if (alt) mods |= ModifierKeys.Alt;
        if (shift) mods |= ModifierKeys.Shift;
        if (win) mods |= ModifierKeys.Windows;

        ApplyCaptured(HotkeyInfo.Format(mods, key), pidBefore);
    }

    void ApplyCaptured(string? text, uint pidBefore)
    {
        if (text == null)
        {
            StatusText.Text = Loc.Get("HotkeyBadKey");
            StatusText.Foreground = Theme.Brush("Danger");
            return;
        }
        var (valid, reason) = HotkeyInfo.Validate(text);
        if (!valid)
        {
            ComboText.Text = text;
            StatusText.Text = reason;
            StatusText.Foreground = Theme.Brush("Danger");
            return;
        }
        var err = Apply?.Invoke(text);
        if (err == null)
        {
            Hotkey = text;
            _lastRegistered = true;
            ComboText.Text = text;
            StatusText.Text = Loc.Get("HotkeyOk");
            StatusText.Foreground = OkBrushFrozen;
            return;
        }

        // 注册失败：先显示宿主给的/知识库提示，800ms 内观察是否有程序响应这次按键
        _lastRegistered = false;
        ComboText.Text = text;
        _conflictBase = err;
        StatusText.Text = _conflictBase;
        StatusText.Foreground = Theme.Brush("Danger");
        _fgPidBefore = pidBefore;
        _comboPending = true;
        _responderTimer.Stop();
        _responderTimer.Start();
    }

    void CheckResponder()
    {
        _responderTimer.Stop();
        if (!_comboPending || _conflictBase == null) return;
        _comboPending = false;

        var fg = Native.GetForegroundWindow();
        uint pidNow = 0;
        if (fg == IntPtr.Zero) return;
        Native.GetWindowThreadProcessId(fg, ref pidNow);
        if (pidNow == 0 || pidNow == _fgPidBefore) return;

        try
        {
            using var p = Process.GetProcessById((int)pidNow);
            if (p.Id == Environment.ProcessId) return;
            var desc = p.ProcessName;
            try
            {
                var vi = p.MainModule?.FileVersionInfo;
                if (!string.IsNullOrWhiteSpace(vi?.FileDescription))
                    desc = vi!.FileDescription!.Trim();
            }
            catch { /* 系统进程无权限读模块信息 */ }
            StatusText.Text = _conflictBase + "\n" + Loc.F("HotkeyResponderFmt", desc);
        }
        catch { /* 进程已退出 */ }
    }

    void OffBtn_Click(object sender, RoutedEventArgs e) => Disable?.Invoke();
}
