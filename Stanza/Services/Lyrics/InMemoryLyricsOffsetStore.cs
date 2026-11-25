namespace Stanza.Services.Lyrics;

/// <summary>数据库不可用时保留本次运行中的单曲偏移。</summary>
internal sealed class InMemoryLyricsOffsetStore : ILyricsOffsetStore
{
    private readonly Dictionary<string, int> _items = [];

    public void Initialize() { }

    public int Get(string key) => _items.GetValueOrDefault(key);

    public bool Set(string key, int offsetMs)
    {
        if (offsetMs == 0)
            _items.Remove(key);
        else
            _items[key] = offsetMs;
        return true;
    }
}
