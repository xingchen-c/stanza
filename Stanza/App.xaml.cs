using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using Stanza.Services;

namespace Stanza;

/// <summary>
///     应用入口：数据目录迁移、全局异常兜底、单实例保护和日志生命周期。
/// </summary>
public partial class App
{
    private static Mutex? _singleInstanceMutex;

    /// <summary>启动入口：迁移旧数据、注册异常兜底并建立单实例互斥体。</summary>
    protected override void OnStartup(StartupEventArgs e)
    {
        // 一次性迁移旧版数据目录（改名前叫 FluentLyrics），必须赶在 AppLog 首次建目录之前
        MigrateLegacyAppData();

        // 全局异常兜底：任何线程的未处理异常都落盘并写崩溃报告，便于反馈定位
        AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        // 单实例保护：防止两个歌词窗口叠在任务栏上
        _singleInstanceMutex = new Mutex(true, @"Local\Stanza_SingleInstance", out var createdNew);
        if (!createdNew)
        {
            AppLog.Info("检测到已有实例，本次启动退出");
            Shutdown();
            return;
        }

        AppLog.Info($"应用启动 v{Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "?"}");
        base.OnStartup(e);
    }

    /// <summary>退出清理：释放单实例互斥体并冲刷异步日志。</summary>
    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            _singleInstanceMutex?.ReleaseMutex();
        }
        catch
        {
            // 不持有互斥体时释放会抛异常，忽略
        }

        AppLog.Info("应用退出");
        AppLog.Flush(); // 冲刷异步缓冲，确保最后一行落盘
        base.OnExit(e);
    }

    /// <summary>
    ///     一次性迁移旧版数据目录 %APPDATA%\FluentLyrics → %APPDATA%\Stanza（改名前叫 FluentLyrics）。
    ///     仅当旧目录存在且新目录尚未建立时执行，幂等；失败静默，不影响启动。
    /// </summary>
    private static void MigrateLegacyAppData()
    {
        try
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var oldDir = Path.Combine(appData, "FluentLyrics");
            var newDir = Path.Combine(appData, "Stanza");
            if (!Directory.Exists(oldDir) || Directory.Exists(newDir))
                return;

            Directory.CreateDirectory(newDir);
            foreach (var file in Directory.EnumerateFiles(oldDir, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(oldDir, file);
                var dest = Path.Combine(newDir, relative);
                var destDir = Path.GetDirectoryName(dest);
                if (destDir is not null)
                    Directory.CreateDirectory(destDir);
                File.Copy(file, dest, overwrite: false);
            }
        }
        catch
        {
            // 迁移失败不影响启动，用户可手动重配
        }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        WriteCrashReport("UI线程", e.Exception);
        // 歌词工具是常驻小窗，UI 异常兜底后继续运行；主循环（ticker）会自行恢复显示
        e.Handled = true;
    }

    private void OnAppDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        WriteCrashReport("AppDomain", e.ExceptionObject as Exception);
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        WriteCrashReport("Task", e.Exception);
        e.SetObserved(); // 防止后台任务异常直接拖垮进程
    }

    /// <summary>把异常与运行环境写进崩溃报告，方便用户反馈时直接拷文件</summary>
    private static void WriteCrashReport(string source, Exception? ex)
    {
        try
        {
            AppLog.Error($"[崩溃] 来源={source}", ex);

            var path = Path.Combine(AppLog.LogDirectory, $"crash-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
            var sb = new StringBuilder();
            sb.AppendLine($"时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}");
            sb.AppendLine($"来源: {source}");
            sb.AppendLine($"版本: {Assembly.GetExecutingAssembly().GetName().Version?.ToString(3)}");
            sb.AppendLine($"系统: {Environment.OSVersion}");
            sb.AppendLine($"进程: {Environment.ProcessPath}");
            sb.AppendLine($"异常: {ex}");
            File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
        }
        catch
        {
            // 崩溃报告本身失败不再递归
        }
    }
}
