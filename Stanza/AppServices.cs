using Stanza.Services.Lyrics;
using Stanza.Services.Storage;
using Stanza.Services;

namespace Stanza;

/// <summary>应用组合根：显式创建并持有基础设施实例，避免业务代码依赖静态全局状态。</summary>
internal sealed class AppServices
{
    public AppServices(string? databasePath = null)
    {
        Database = new LocalDatabase(databasePath);
        try
        {
            Database.Initialize();
            LyricsCache = new SqliteLyricsCacheStore(Database);
            LyricsChoices = new SqliteLyricsChoiceStore(Database);
            LyricsCache.Initialize();
            LyricsChoices.Initialize();
        }
        catch (Exception ex)
        {
            AppLog.Warn($"本地数据库初始化失败，改用内存存储: {ex.Message}");
            LyricsCache = new InMemoryLyricsCacheStore();
            LyricsChoices = new InMemoryLyricsChoiceStore();
        }
    }

    public LocalDatabase Database { get; }

    public ILyricsCacheStore LyricsCache { get; }

    public ILyricsChoiceStore LyricsChoices { get; }
}
