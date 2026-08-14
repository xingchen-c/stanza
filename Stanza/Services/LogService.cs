using System.IO;
using System.Text;

namespace Stanza.Services;

/// <summary>
///     应用日志：线程安全地写入 %APPDATA%\Stanza\logs\ 下的按天滚动日志文件，
///     自动保留最近 7 天、单文件超过 8MB 后滚动到 .1/.2 后缀文件。
///     全局异常与关键事件都会经过这里落盘，用户反馈问题时直接拷日志目录即可。
/// </summary>
public static class AppLog
{
    /// <summary>日志级别：低于该级别的日志不写文件（调试器输出始终保留）</summary>
#if DEBUG
    public static LogLevel MinimumLevel { get; set; } = LogLevel.Debug;
#else
    public static LogLevel MinimumLevel { get; set; } = LogLevel.Info;
#endif

    public enum LogLevel
    {
        Debug,
        Info,
        Warn,
        Error
    }

    /// <summary>保留日志天数</summary>
    private const int KeepDays = 7;

    /// <summary>单文件超过该大小（字节）后滚动</summary>
    private const long MaxLogSize = 8 * 1024 * 1024;

    private static readonly Lock Sync = new();
    private static string _currentDate = string.Empty;
    private static string _currentFile = string.Empty;
    private static long _currentSize;
    private static int _rollIndex;

    static AppLog()
    {
        try
        {
            Directory.CreateDirectory(LogDirectory);
            CleanupOldLogs();
        }
        catch
        {
            // 日志目录初始化失败不能影响主流程
        }
    }

    /// <summary>日志根目录（托盘"打开日志目录"也指向这里）</summary>
    public static string LogDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Stanza", "logs");

    public static void Debug(string message)
    {
        Write("DEBUG", message);
    }

    public static void Info(string message)
    {
        Write("INFO", message);
    }

    public static void Warn(string message)
    {
        Write("WARN", message);
    }

    public static void Error(string message, Exception? ex = null)
    {
        Write("ERROR", ex is null ? message : $"{message}{Environment.NewLine}{ex}");
    }

    private static void Write(string level, string message)
    {
        // 调试器输出始终保留，Rider/VS 里依然看得到
        // 全限定名：本类有静态方法 Debug，裸写 Debug 会被解析成方法组而不是 System.Diagnostics.Debug
        System.Diagnostics.Debug.WriteLine($"[Stanza] {message}");

        // 低于当前级别（如发行版默认 Info 时的 DEBUG）不落盘，避免高频调试日志拖累磁盘 IO
        if (!ShouldWriteFile(level))
            return;

        try
        {
            lock (Sync)
            {
                var now = DateTime.Now;
                var date = now.ToString("yyyyMMdd");
                if (date != _currentDate)
                {
                    _currentDate = date;
                    _currentFile = Path.Combine(LogDirectory, $"fluentlyrics-{date}.log");
                    _currentSize = SafeFileLength(_currentFile);
                    _rollIndex = 0;
                }

                if (_currentSize > MaxLogSize)
                {
                    _currentFile = Path.Combine(LogDirectory, $"fluentlyrics-{date}.log.{++_rollIndex}");
                    _currentSize = SafeFileLength(_currentFile);
                }

                var line = $"{now:yyyy-MM-dd HH:mm:ss.fff} [{level}] [Stanza] {message}{Environment.NewLine}";
                File.AppendAllText(_currentFile, line, Encoding.UTF8);
                _currentSize += Encoding.UTF8.GetByteCount(line);
            }
        }
        catch
        {
            // 日志写入失败绝不能影响主功能
        }
    }

    /// <summary>该级别是否达到 MinimumLevel（DEBUG &lt; INFO &lt; WARN &lt; ERROR）</summary>
    private static bool ShouldWriteFile(string level)
    {
        return Rank(level) >= Rank(MinimumLevel.ToString());
    }

    private static int Rank(string level)
    {
        return level.ToUpperInvariant() switch
        {
            "DEBUG" => 0,
            "INFO" => 1,
            "WARN" => 2,
            "ERROR" => 3,
            _ => 1
        };
    }

    private static long SafeFileLength(string path)
    {
        try
        {
            return File.Exists(path) ? new FileInfo(path).Length : 0;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>启动时清理超过保留天数的旧日志</summary>
    private static void CleanupOldLogs()
    {
        try
        {
            if (!Directory.Exists(LogDirectory))
                return;

            var threshold = DateTime.Now.AddDays(-KeepDays);
            foreach (var file in Directory.EnumerateFiles(LogDirectory, "fluentlyrics-*.log*"))
            {
                var info = new FileInfo(file);
                if (info.LastWriteTime < threshold)
                    File.Delete(file);
            }
        }
        catch
        {
            // 清理失败不影响主流程
        }
    }
}
