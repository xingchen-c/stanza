using Stanza.Core.Models;

namespace Stanza.Infrastructure.Lyrics;

/// <summary>数据库不可用时的内存歌词缓存。</summary>
internal sealed class InMemoryLyricsCacheStore : ILyricsCacheStore
{
    private readonly Dictionary<string, LyricsCacheHit> _items = [];

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
        _items[key] = new(status, [.. lines]);
    }

    public Task<LyricsCacheHit?> GetAsync(string key)
        => Task.FromResult(_items.TryGetValue(key, out var hit) ? (LyricsCacheHit?)hit : null);

    public Task PutDecisionAsync(string key, LyricsLoadResult status, IReadOnlyList<LyricLine> lines,
        LyricsDecisionEvidence decision)
    {
        _items[key] = new(status, [.. lines], decision);
        return Task.CompletedTask;
    }

    public void Clear() => _items.Clear();

    public void Remove(string key) => _items.Remove(key);

    public Task RemoveAsync(string key)
    {
        Remove(key);
        return Task.CompletedTask;
    }
}
