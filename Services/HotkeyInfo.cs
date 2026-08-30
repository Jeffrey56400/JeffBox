using System.Diagnostics;
using System.Windows.Input;

namespace TodoApp.Services;

/// <summary>
/// 全局快捷键：任意 "Ctrl+Alt+Shift+Win+键" 组合的解析与校验，
/// 以及注册失败时的冲突识别（常见软件默认热键知识库 + 运行进程佐证）。
/// </summary>
public static class HotkeyInfo
{
    public const uint MOD_ALT = 0x1;
    public const uint MOD_CONTROL = 0x2;
    public const uint MOD_SHIFT = 0x4;
    public const uint MOD_WIN = 0x8;
    public const uint MOD_NOREPEAT = 0x4000;

    static bool En => Loc.Lang == "en";

    // ---------- 解析 / 格式化 ----------

    /// <summary>"Ctrl+Alt+T" 之类文本 → RegisterHotKey 参数；识别失败返回 false</summary>
    public static bool TryParse(string text, out uint mods, out uint vk)
    {
        mods = 0;
        vk = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var sawKey = false;
        foreach (var raw in text.Split('+'))
        {
            var t = raw.Trim();
            if (t.Length == 0) return false;
            if (t.Equals("Ctrl", StringComparison.OrdinalIgnoreCase) ||
                t.Equals("Control", StringComparison.OrdinalIgnoreCase))
                mods |= MOD_CONTROL;
            else if (t.Equals("Alt", StringComparison.OrdinalIgnoreCase))
                mods |= MOD_ALT;
            else if (t.Equals("Shift", StringComparison.OrdinalIgnoreCase))
                mods |= MOD_SHIFT;
            else if (t.Equals("Win", StringComparison.OrdinalIgnoreCase) ||
                     t.Equals("Windows", StringComparison.OrdinalIgnoreCase) ||
                     t.Equals("Meta", StringComparison.OrdinalIgnoreCase))
                mods |= MOD_WIN;
            else if (!sawKey && TryParseKey(t, out var key))
            {
                vk = (uint)KeyInterop.VirtualKeyFromKey(key);
                sawKey = true;
            }
            else return false;
        }
        return sawKey && vk > 0;
    }

    /// <summary>修饰键 + 主键 → 规范文本，如 "Ctrl+Alt+T"；不合法返回 null</summary>
    public static string? Format(ModifierKeys modifiers, Key key)
    {
        if (!IsUsableKey(key)) return null;
        var parts = new List<string>();
        if (modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        parts.Add(KeyName(key));
        return string.Join("+", parts);
    }

    static readonly Dictionary<string, Key> NamedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Space"] = Key.Space,
        ["Tab"] = Key.Tab,
        ["PrtSc"] = Key.PrintScreen,
        ["PrintScreen"] = Key.PrintScreen,
        ["-"] = Key.OemMinus,
        ["="] = Key.OemPlus,
        ["["] = Key.OemOpenBrackets,
        ["]"] = Key.OemCloseBrackets,
        [";"] = Key.OemSemicolon,
        ["'"] = Key.OemQuotes,
        ["`"] = Key.OemTilde,
        ["\\"] = Key.OemBackslash,
        [","] = Key.OemComma,
        ["."] = Key.OemPeriod,
        ["/"] = Key.OemQuestion,
    };

    static bool TryParseKey(string t, out Key key)
    {
        key = Key.None;
        if (t.Length == 1)
        {
            var c = char.ToUpperInvariant(t[0]);
            if (c is >= 'A' and <= 'Z') { key = (Key)(Key.A + c - 'A'); return true; }
            if (c is >= '0' and <= '9') { key = (Key)(Key.D0 + c - '0'); return true; }
        }
        if (t.Length >= 2 && (t[0] is 'F' or 'f') &&
            int.TryParse(t[1..], out var fn) && fn is >= 1 and <= 12)
        {
            key = (Key)(Key.F1 + fn - 1);
            return true;
        }
        return NamedKeys.TryGetValue(t, out key);
    }

    static bool IsUsableKey(Key k) =>
        k is (>= Key.A and <= Key.Z) or (>= Key.D0 and <= Key.D9) or (>= Key.F1 and <= Key.F12)
            or Key.Space or Key.Tab or Key.PrintScreen
            or Key.OemMinus or Key.OemPlus or Key.OemOpenBrackets or Key.OemCloseBrackets
            or Key.OemSemicolon or Key.OemQuotes or Key.OemTilde or Key.OemBackslash
            or Key.OemComma or Key.OemPeriod or Key.OemQuestion;

    static string KeyName(Key k) =>
        k switch
        {
            >= Key.D0 and <= Key.D9 => ((int)(k - Key.D0)).ToString(),
            Key.Space => "Space",
            Key.Tab => "Tab",
            Key.PrintScreen => "PrtSc",
            Key.OemMinus => "-",
            Key.OemPlus => "=",
            Key.OemOpenBrackets => "[",
            Key.OemCloseBrackets => "]",
            Key.OemSemicolon => ";",
            Key.OemQuotes => "'",
            Key.OemTilde => "`",
            Key.OemBackslash => "\\",
            Key.OemComma => ",",
            Key.OemPeriod => ".",
            Key.OemQuestion => "/",
            _ => k.ToString(),
        };

    // ---------- 校验（注册前） ----------

    /// <summary>返回 (是否允许尝试注册, 拒绝原因)；拦截系统保留组合和纯 Win 组合</summary>
    public static (bool Ok, string? Reason) Validate(string text)
    {
        if (!TryParse(text, out var mods, out _))
            return (false, Loc.Get("HotkeyBadKey"));

        if (Reserved.TryGetValue(text, out var why))
            return (false, Loc.F("HotkeyReservedFmt", text, En ? why.en : why.zh));

        var hasCtrl = (mods & MOD_CONTROL) != 0;
        var hasAlt = (mods & MOD_ALT) != 0;
        var hasWin = (mods & MOD_WIN) != 0;
        if (!hasCtrl && !hasAlt && hasWin)
            return (false, Loc.Get("HotkeyWinOnly"));
        if (!hasCtrl && !hasAlt)
            return (false, Loc.Get("HotkeyNoMod"));
        return (true, null);
    }

    static readonly Dictionary<string, (string zh, string en)> Reserved = new()
    {
        ["Alt+F4"] = ("关闭窗口", "close window"),
        ["Alt+Tab"] = ("窗口切换", "switch windows"),
        ["Alt+Esc"] = ("窗口切换", "switch windows"),
        ["Ctrl+Esc"] = ("开始菜单", "Start menu"),
        ["Ctrl+Shift+Esc"] = ("任务管理器", "Task Manager"),
        ["Ctrl+Alt+Del"] = ("系统安全界面", "secure screen"),
    };

    // ---------- 冲突识别（注册失败后） ----------

    sealed class Kb
    {
        public required string Combo;
        public string ZhApp = "", EnApp = "";
        public string ZhDesc = "", EnDesc = "";
        public string[] Procs = Array.Empty<string>();
    }

    static readonly Kb[] Knowledge =
    {
        new() { Combo = "Alt+A", ZhApp = "微信", EnApp = "WeChat", ZhDesc = "截图热键", EnDesc = "screenshot hotkey", Procs = new[] { "WeChat", "Weixin" } },
        new() { Combo = "Ctrl+Alt+A", ZhApp = "QQ", EnApp = "QQ", ZhDesc = "截图热键", EnDesc = "screenshot hotkey", Procs = new[] { "QQ", "QQExternal" } },
        new() { Combo = "Ctrl+Shift+A", ZhApp = "企业微信 / 钉钉", EnApp = "WeChat Work / DingTalk", ZhDesc = "截图热键", EnDesc = "screenshot hotkey", Procs = new[] { "WXWork", "DingTalk", "DingtalkLauncher" } },
        new() { Combo = "Ctrl+Alt+Z", ZhApp = "QQ", EnApp = "QQ", ZhDesc = "提取消息热键", EnDesc = "message hotkey", Procs = new[] { "QQ", "QQExternal" } },
        new() { Combo = "Ctrl+Alt+P", ZhApp = "网易云音乐", EnApp = "NetEase Cloud Music", ZhDesc = "全局播放/暂停热键", EnDesc = "play/pause hotkey", Procs = new[] { "cloudmusic" } },
        new() { Combo = "F1", ZhApp = "Snipaste", EnApp = "Snipaste", ZhDesc = "截图热键", EnDesc = "screenshot hotkey", Procs = new[] { "Snipaste", "Snipaste64" } },
        new() { Combo = "Alt+Z", ZhApp = "NVIDIA GeForce Experience", EnApp = "NVIDIA GeForce Experience", ZhDesc = "游戏内覆盖热键", EnDesc = "in-game overlay hotkey", Procs = new[] { "nvidia share", "nvcontainer" } },
        new() { Combo = "Ctrl+Space", ZhApp = "输入法", EnApp = "the IME", ZhDesc = "中英文切换", EnDesc = "Chinese/English toggle" },
    };

    /// <summary>RegisterHotKey 失败后调用：结合知识库与正在运行的进程，给出人话原因</summary>
    public static string ConflictHint(string text)
    {
        var kb = Knowledge.FirstOrDefault(k =>
            k.Combo.Equals(text, StringComparison.OrdinalIgnoreCase));
        if (kb != null)
        {
            var app = En ? kb.EnApp : kb.ZhApp;
            var desc = En ? kb.EnDesc : kb.ZhDesc;
            if (kb.Procs.Length > 0 && IsAnyRunning(kb.Procs))
                return Loc.F("HotkeyConflictRunningFmt", text, app, desc);
            return Loc.F("HotkeyConflictDefaultFmt", text, app, desc);
        }
        return Loc.Get("HotkeyConflictUnknown");
    }

    static bool IsAnyRunning(string[] procs)
    {
        try
        {
            foreach (var name in procs)
                if (Process.GetProcessesByName(name).Length > 0)
                    return true;
        }
        catch { }
        return false;
    }
}
