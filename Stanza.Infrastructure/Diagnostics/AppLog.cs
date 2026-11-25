using System.IO;
using System.Text;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace Stanza.Infrastructure.Diagnostics;

/// <summary>
///     应用日志：线程安全地写入应用数据目录 logs/ 下的按天滚动日志文件，
///     自动保留最近 7 天、单文件超过 8MB 后滚动到 .1/.2 后缀文件。
///     全局异常与关键事件都会经过这里落盘，用户反馈问题时直接拷日志目录即可。
/// </summary>
public static class AppLog
{
    public static void BindCore()
        => Stanza.Core.Diagnostics.CoreLog.Write = (level, exception, template, args)
            => Write(level switch
            {
                "Debug" => LogEventLevel.Debug,
                "Warn" => LogEventLevel.Warning,
                "Error" => LogEventLevel.Error,
                _ => LogEventLevel.Information
            }, exception, template, args);
    /// <summary>日志级别：低于该级别的日志不写文件（调试器输出始终保留）</summary>
    private static readonly LoggingLevelSwitch LevelSwitch = new(
#if DEBUG
        LogEventLevel.Debug
#else
        LogEventLevel.Information
#endif
    );

    /// <summary>按字符串设置日志级别（设置加载后调用；无法识别的值按 Info 处理）</summary>
    public static void ApplyLevel(string? level)
    {
        MinimumLevel = level?.Trim().ToLowerInvariant() switch
        {
            "debug" => LogLevel.Debug,
            "warn" => LogLevel.Warn,
            "error" => LogLevel.Error,
            _ => LogLevel.Info
        };
    }

    /// <summary>日志级别（保留原 API：设置窗口/兼容模式可调）</summary>
    public static LogLevel MinimumLevel
    {
        get => LevelSwitch.MinimumLevel switch
        {
            LogEventLevel.Debug => LogLevel.Debug,
            LogEventLevel.Information => LogLevel.Info,
            LogEventLevel.Warning => LogLevel.Warn,
            _ => LogLevel.Error
        };
        set => LevelSwitch.MinimumLevel = value switch
        {
            LogLevel.Debug => LogEventLevel.Debug,
            LogLevel.Info => LogEventLevel.Information,
            LogLevel.Warn => LogEventLevel.Warning,
            _ => LogEventLevel.Error
        };
    }

    /// <summary>应用日志级别。</summary>
    public enum LogLevel
    {
        /// <summary>输出全部诊断信息。</summary>
        Debug,

        /// <summary>输出常规运行信息。</summary>
        Info,

        /// <summary>只输出警告和错误。</summary>
        Warn,

        /// <summary>只输出错误。</summary>
        Error
    }

    /// <summary>保留日志天数</summary>
    private const int KeepDays = 7;

    /// <summary>单文件超过该大小（字节）后滚动</summary>
    private const long MaxLogSize = 8 * 1024 * 1024;

    /// <summary>日志根目录（托盘"打开日志目录"也指向这里）</summary>
    public static string LogDirectory { get; } = AppDataPaths.LogDirectory;

    static AppLog()
    {
        try
        {
            Directory.CreateDirectory(LogDirectory);
            CleanupLegacyLogs(); // 清旧的 fluentlyrics-*.log（改名遗留）与过期日志

            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.ControlledBy(LevelSwitch)
                .WriteTo.Async(a => a.File(
                    path: Path.Combine(LogDirectory, "stanza-.log"),
                    rollingInterval: RollingInterval.Day, // stanza-20260901.log
                    fileSizeLimitBytes: MaxLogSize, // 单文件 8MB 上限
                    rollOnFileSizeLimit: true, // 超限滚 _001 后缀
                    retainedFileCountLimit: KeepDays, // 保留最近 14 个文件
                    outputTemplate:
                    "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] [Stanza] {Message:lj}{NewLine}{Exception}"))
                .CreateLogger();
        }
        catch
        {
            Log.Logger = Serilog.Core.Logger.None; // 日志初始化失败绝不能影响主流程
        }
    }

    // ── 传统重载（现有全部调用点零改动）──
    /// <summary>写入 Debug 日志。</summary>
    public static void Debug(string message) => Write(LogEventLevel.Debug, null, message);

    /// <summary>写入 Info 日志。</summary>
    public static void Info(string message) => Write(LogEventLevel.Information, null, message);

    /// <summary>写入 Warn 日志。</summary>
    public static void Warn(string message) => Write(LogEventLevel.Warning, null, message);

    /// <summary>写入 Error 日志，可附带异常。</summary>
    public static void Error(string message, Exception? ex = null) => Write(LogEventLevel.Error, ex, message);

    // ── 结构化重载（新代码用：命名属性可被检索/导出）──
    /// <summary>写入带命名属性的 Debug 日志。</summary>
    public static void Debug(string template, params object?[] args) => Write(LogEventLevel.Debug, null, template, args);

    /// <summary>写入带命名属性的 Info 日志。</summary>
    public static void Info(string template, params object?[] args) => Write(LogEventLevel.Information, null, template, args);

    /// <summary>写入带命名属性的 Warn 日志。</summary>
    public static void Warn(string template, params object?[] args) => Write(LogEventLevel.Warning, null, template, args);

    /// <summary>写入带命名属性和异常的 Error 日志。</summary>
    public static void Error(Exception? ex, string template, params object?[] args) => Write(LogEventLevel.Error, ex, template, args);

    /// <summary>退出时冲刷缓冲（App.OnExit 调用）</summary>
    public static void Flush() => Log.CloseAndFlush();

    private static void Write(LogEventLevel level, Exception? ex, string template, object?[]? args = null)
    {
        // 调试器输出始终保留（全限定名：本类有静态方法 Debug，裸写会被解析成方法组）
        System.Diagnostics.Debug.WriteLine($"[Stanza] {template}");
        var logger = Log.Logger;
        if (!logger.IsEnabled(level))
            return;
        logger.Write(level, ex, template, args ?? []);
    }

    /// <summary>清理改名前遗留的 fluentlyrics-*.log 与 7 天前的旧日志（Serilog 只管自己的滚动文件）</summary>
    private static void CleanupLegacyLogs()
    {
        try
        {
            if (!Directory.Exists(LogDirectory))
                return;
            var threshold = DateTime.Now.AddDays(-7);
            foreach (var file in Directory.EnumerateFiles(LogDirectory, "*.log*"))
            {
                var info = new FileInfo(file);
                if (info.Name.StartsWith("fluentlyrics-", StringComparison.OrdinalIgnoreCase) ||
                    info.LastWriteTime < threshold)
                    File.Delete(file);
            }
        }
        catch
        {
            // 清理失败不影响主流程
        }
    }
}
