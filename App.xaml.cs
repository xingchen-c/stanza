using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using FluentLyrics.Services;

namespace FluentLyrics;

/// <summary>
///     Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    private static Mutex? _singleInstanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        // 全局异常兜底：任何线程的未处理异常都落盘并写崩溃报告，便于反馈定位
        AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        // 单实例保护：防止两个歌词窗口叠在任务栏上
        _singleInstanceMutex = new Mutex(true, @"Local\FluentLyrics_SingleInstance", out var createdNew);
        if (!createdNew)
        {
            AppLog.Info("检测到已有实例，本次启动退出");
            Shutdown();
            return;
        }

        AppLog.Info($"应用启动 v{Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "?"}");
        base.OnStartup(e);
    }

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
        base.OnExit(e);
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