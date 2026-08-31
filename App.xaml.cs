using System.IO;
using System.Text;
using System.Threading;
using System.Windows;

namespace TodoApp;

public partial class App : System.Windows.Application
{
    const string MutexName = @"Local\JeffBox.SingleInstance";
    const string SignalName = @"Local\JeffBox.ShowSignal";

    public static bool StartMinimized;
    public static string? PendingOpenFile;

    internal static void LogCrash(string source, Exception? ex)
    {
        try
        {
            var msg = ex == null ? "(null)" : ex.ToString();
            if (ex?.InnerException != null) msg += "\n-- Inner --\n" + ex.InnerException;
            System.IO.File.AppendAllText(
                System.IO.Path.Combine(Services.AppPaths.DataDir, "crash.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {source}\n{msg}\n---\n");
        }
        catch { }
    }

    static string OpenRequestPath =>
System.IO.Path.Combine(Services.AppPaths.DataDir, "open_request.txt");

    Mutex? _mutex;
    EventWaitHandle? _showSignal;
    System.Threading.CancellationTokenSource? _signalCts;
    bool _ownsMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        Services.AppPaths.EnsureMigrated(); // v1.0 更名 JeffBox：首启迁移旧 TodoApp 数据
        // 崩溃捕获：未处理异常写日志而不是静默闪退，便于定位（%APPDATA%\JeffBox\crash.log）
        System.Windows.Application.Current.DispatcherUnhandledException += (_, ex) =>
        {
            LogCrash("UI", ex.Exception);
            ex.Handled = true; // 记录后吞掉，避免输入过程闪退丢内容
        };
        AppDomain.CurrentDomain.UnhandledException += (_, ex) =>
            LogCrash("Domain", ex.ExceptionObject as Exception);
        try { System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance); } catch { }
        // 先解析命令行（二次实例也要用它传递文件），再进入单实例判断
        foreach (var a in e.Args)
        {
            if (a.StartsWith("-")) continue;
            var ext = System.IO.Path.GetExtension(a).ToLowerInvariant();
            if (File.Exists(a) && ext is ".md" or ".markdown" or ".txt")
            {
                PendingOpenFile = System.IO.Path.GetFullPath(a);
                break;
            }
        }

        // 单实例：重复启动时唤醒已有窗口，避免托盘图标和全局热键重复注册
        _mutex = new Mutex(true, MutexName, out var createdNew);
        if (!createdNew)
        {
            // 已有实例：把要打开的文件写入请求文件，再唤醒它
            if (PendingOpenFile != null)
            {
                try
                {
                    System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(OpenRequestPath)!);
                    System.IO.File.WriteAllText(OpenRequestPath, PendingOpenFile);
                }
                catch { }
            }
            try { EventWaitHandle.OpenExisting(SignalName).Set(); } catch { }
            Shutdown();
            return;
        }
        _ownsMutex = true;

        Services.Theme.EnsureRegistered(); // 主题画刷必须先于窗口创建注册

        // 把本应用注册进系统"打开方式"列表（仅添加选项，不改默认关联）
        try
        {
            using (var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(
                @"Software\Classes\Applications\JeffBox.exe\shell\open\command"))
                k.SetValue(null, "\"" + Environment.ProcessPath + "\" \"%1\"");
            using (var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(
                @"Software\Classes\Applications\JeffBox.exe"))
                k.SetValue("FriendlyAppName", "Toolbox 工具箱");
        }
        catch { }
        _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, SignalName);
        StartMinimized = e.Args.Contains("--minimized", StringComparer.OrdinalIgnoreCase);

        base.OnStartup(e);

        _signalCts = new System.Threading.CancellationTokenSource();
        var ct = _signalCts.Token;
        new Thread(() =>
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    if (_showSignal!.WaitOne(500))
                        Dispatcher.BeginInvoke(() =>
                        {
                            var win = MainWindow as MainWindow;
                            if (win == null) return;
                            win.ShowFromTray();
                            win.ConsumeOpenRequest();
                        });
                }
            }
            catch (ObjectDisposedException) { }
        })
        { IsBackground = true }.Start();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _signalCts?.Cancel();
        // 不 Dispose 句柄/互斥体：后台线程可能仍在 WaitOne，进程退出时由系统回收更安全
        base.OnExit(e);
    }
}
