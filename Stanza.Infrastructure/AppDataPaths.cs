using System.IO;

namespace Stanza;

/// <summary>进程启动时确定数据根目录，设置、歌词和日志共享同一位置。</summary>
internal static class AppDataPaths
{
    private static readonly string? DirectoryOverride = Environment.GetEnvironmentVariable("STANZA_DATA_DIR");

    public static bool IsCustomDirectory { get; } = !string.IsNullOrWhiteSpace(DirectoryOverride);

    public static string DataDirectory { get; } = ResolveDataDirectory(DirectoryOverride);

    public static string SettingsPath { get; } = Path.Combine(DataDirectory, "settings.json");

    public static string DatabasePath { get; } = Path.Combine(DataDirectory, "stanza.db");

    public static string LogDirectory { get; } = Path.Combine(DataDirectory, "logs");

    internal static string ResolveDataDirectory(string? directory) => string.IsNullOrWhiteSpace(directory)
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Stanza")
        : Path.GetFullPath(Environment.ExpandEnvironmentVariables(directory), AppContext.BaseDirectory);
}
