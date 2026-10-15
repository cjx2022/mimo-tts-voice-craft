using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using MimoTtsVoiceCraft.Helpers;

namespace MimoTtsVoiceCraft;

public partial class App : Application
{
    /// <summary>崩溃日志位置：%LOCALAPPDATA%\MimoTtsVoiceCraft\error.log</summary>
    public static string CrashLog => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MimoTtsVoiceCraft", "error.log");

    /// <summary>--ghost 自检：窗口不激活、不进任务栏、全透明</summary>
    public static bool GhostMode { get; private set; }

    /// <summary>--screenshot=&lt;path&gt; 自检：渲染窗口为 PNG 后退出</summary>
    public static string ScreenshotPath { get; private set; }

    /// <summary>--page=design|settings 自检：截图前直入目标界面</summary>
    public static string Page { get; private set; }

    /// <summary>--opaque 自检：配合 --ghost 使用，离屏但不降透明度（供 PrintWindow 采样）</summary>
    public static bool OpaqueProbe { get; private set; }

    /// <summary>--audiotest=&lt;path&gt; 静音自检：真实合成一次并验证播放器行为，结果写文件</summary>
    public static string AudioTestPath { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        foreach (var a in e.Args)
        {
            if (a == "--ghost") GhostMode = true;
            else if (a == "--opaque") OpaqueProbe = true;
            else if (a.StartsWith("--audiotest=", StringComparison.OrdinalIgnoreCase))
                AudioTestPath = a.Substring("--audiotest=".Length);
            else if (a.StartsWith("--screenshot=", StringComparison.OrdinalIgnoreCase))
                ScreenshotPath = a.Substring("--screenshot=".Length);
            else if (a.StartsWith("--page=", StringComparison.OrdinalIgnoreCase))
                Page = a.Substring("--page=".Length);
        }

        AppSettings.Load();

        // 软件渲染：不创建 D3D 设备 → 显卡用户态驱动（igd12dxva64/igc64 等约 150MB+）
        // 的页不会被 fault in。必须在创建任何 Window 之前设置，否则无效。
        if (AppSettings.SoftwareRendering)
            RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;

        DispatcherUnhandledException += OnUnhandled;
        AppDomain.CurrentDomain.UnhandledException += (s, args) =>
            WriteCrash("AppDomain", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (s, args) =>
        {
            WriteCrash("Task", args.Exception);
            args.SetObserved();
        };

        WriteCrashMark($"启动 {GetType().Assembly.GetName().Version}" +
                       $" | 渲染={(AppSettings.SoftwareRendering ? "Software" : "Hardware")}" +
                       $" | 最小化回收={(AppSettings.TrimOnMinimize ? "on" : "off")}" +
                       $" | 配置={AppSettings.Location}");
    }

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        WriteCrash("Dispatcher", e.Exception);
        MessageBox.Show(e.Exception.Message, "出错了", MessageBoxButton.OK, MessageBoxImage.Warning);
        e.Handled = true;
    }

    private static void WriteCrashMark(string text)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CrashLog));
            File.AppendAllText(CrashLog, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] --- {text} ---\n");
        }
        catch { }
    }

    private static void WriteCrash(string src, Exception ex)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CrashLog));
            File.AppendAllText(CrashLog,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] !! {src}\n{ex}\n\n");
        }
        catch { }
    }
}
