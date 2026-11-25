namespace Stanza.Core.Diagnostics;

/// <summary>由组合根绑定的日志端口，内核不依赖文件 sink 或宿主。</summary>
public static class CoreLog
{
    public static Action<string, Exception?, string, object?[]>? Write { get; set; }
    public static void Debug(string message) => Write?.Invoke("Debug", null, message, []);
    public static void Info(string message) => Write?.Invoke("Info", null, message, []);
    public static void Warn(string message) => Write?.Invoke("Warn", null, message, []);
    public static void Warn(string template, params object?[] args) => Write?.Invoke("Warn", null, template, args);
    public static void Error(string message, Exception? exception = null) => Write?.Invoke("Error", exception, message, []);
}
