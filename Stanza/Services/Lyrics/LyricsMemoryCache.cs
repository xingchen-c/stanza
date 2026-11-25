using Stanza.Models;

namespace Stanza.Services.Lyrics;

/// <summary>最近歌词内存缓存；满额时淘汰最久未使用项。</summary>
internal sealed class LyricsMemoryCache
{
    private const int MaxEntries = 20;
    private readonly Dictionary<string, Entry> _items = [];

    public bool TryGet(
        string key,
        out LyricsLoadResult status,
        out List<LyricLine> lines,
        out LyricsDecisionEvidence? decision)
    {
        if (_items.TryGetValue(key, out var entry))
        {
            entry.LastUsedUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            status = entry.Status;
            lines = [.. entry.Lines];
            decision = entry.Decision;
            return true;
        }

        status = LyricsLoadResult.NotFound;
        lines = [];
        decision = null;
        return false;
    }

    public void Put(
        string key,
        LyricsLoadResult status,
        IReadOnlyList<LyricLine> lines,
        LyricsDecisionEvidence? decision = null)
    {
        if (_items.Count >= MaxEntries && !_items.ContainsKey(key))
        {
            var staleKey = _items.OrderBy(kv => kv.Value.LastUsedUnixMs).First().Key;
            _items.Remove(staleKey);
        }

        _items[key] = new Entry
        {
            Status = status,
            Lines = [.. lines],
            Decision = decision,
            LastUsedUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };
    }

    public void Clear() => _items.Clear();

    public void Remove(string key) => _items.Remove(key);

    private sealed class Entry
    {
        public LyricsLoadResult Status;
        public List<LyricLine> Lines = [];
        public LyricsDecisionEvidence? Decision;
        public long LastUsedUnixMs;
    }
}
