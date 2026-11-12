using Stanza.Models;

namespace Stanza.Services.Lyrics;

/// <summary>数据库不可用时的内存歌词缓存。</summary>
internal sealed class InMemoryLyricsCacheStore : ILyricsCacheStore
{
    private readonly Dictionary<string, (LyricsLoadResult Status, List<LyricLine> Lines)> _items = [];

    public void Initialize()
    {
    }

    public bool TryGet(
        string key,
        out LyricsLoadResult status,
        out List<LyricLine> lines)
    {
        if (_items.TryGetValue(key, out var item))
        {
            status = item.Status;
            lines = [.. item.Lines];
            return true;
        }

        status = LyricsLoadResult.NotFound;
        lines = [];
        return false;
    }

    public void Put(
        string key,
        LyricsLoadResult status,
        IReadOnlyList<LyricLine> lines)
    {
        _items[key] = (status, [.. lines]);
    }

    public void Clear() => _items.Clear();
}
