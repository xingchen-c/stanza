using System.IO;
using System.Runtime.CompilerServices;

namespace Stanza.Tests;

internal static class TestEnvironment
{
    public static string DataDirectory { get; } = Path.Combine(
        Path.GetTempPath(), "Stanza.Tests", Guid.NewGuid().ToString("N"));

    // 必须早于业务静态服务初始化，真实日志和默认存储才能一起避开用户目录。
    [ModuleInitializer]
    internal static void Initialize()
    {
        Environment.SetEnvironmentVariable("STANZA_DATA_DIR", DataDirectory);
        Environment.SetEnvironmentVariable("STANZA_DATABASE_PATH", null);
        Environment.SetEnvironmentVariable("STANZA_LEGACY_LYRICS_CACHE_PATH", null);
        Environment.SetEnvironmentVariable("STANZA_LEGACY_CHOICE_PATH", null);
        AppLog.BindCore();
    }
}
