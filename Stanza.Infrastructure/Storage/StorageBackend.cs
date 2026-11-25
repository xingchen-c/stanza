using Stanza.Core.Lyrics;
using Stanza.Infrastructure.Lyrics;
using Stanza.Infrastructure.Storage;

namespace Stanza.Infrastructure.Storage;

/// <summary>初始化本地存储，失败时将三种存储整体回退到内存。</summary>
internal sealed class StorageBackend
{
    public StorageBackend(string? databasePath = null)
    {
        Database = new LocalDatabase(databasePath);
        try
        {
            Database.Initialize();
            LyricsCache = new SqliteLyricsCacheStore(Database);
            LyricsChoices = new SqliteLyricsChoiceStore(Database);
            LyricsOffsets = new SqliteLyricsOffsetStore(Database);
            LyricsCache.Initialize();
            LyricsChoices.Initialize();
            LyricsOffsets.Initialize();
        }
        catch (Exception ex)
        {
            AppLog.Warn($"本地数据库初始化失败，改用内存存储: {ex.Message}");
            LyricsCache = new InMemoryLyricsCacheStore();
            LyricsChoices = new InMemoryLyricsChoiceStore();
            LyricsOffsets = new InMemoryLyricsOffsetStore();
        }
    }

    public LocalDatabase Database { get; }

    public ILyricsCacheStore LyricsCache { get; }

    public ILyricsChoiceStore LyricsChoices { get; }

    public ILyricsOffsetStore LyricsOffsets { get; }
}
