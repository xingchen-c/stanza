using Stanza.Models;

namespace Stanza.Services.Lyrics;

/// <summary>数据库不可用时的内存选择记忆。</summary>
internal sealed class InMemoryLyricsChoiceStore : ILyricsChoiceStore
{
    private readonly Dictionary<string, LyricCandidate> _items = [];

    public void Initialize()
    {
    }

    public bool TryGet(string key, out LyricCandidate? candidate)
        => _items.TryGetValue(key, out candidate);

    public void Put(string key, LyricCandidate candidate) => _items[key] = candidate;

    public void Clear() => _items.Clear();
}
