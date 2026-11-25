namespace Stanza.Core.Lyrics;

/// <summary>当前歌词与请求世代的唯一所有者。</summary>
internal sealed class LyricsContext
{
    private int _generation;
    private readonly Dictionary<string, int> _manualGenerations = [];
    /// <summary>歌词请求世代；同曲手选也会推进，不能用曲目 revision 替代。</summary>
    public int Generation => Volatile.Read(ref _generation);
    public IReadOnlyList<LyricLine> Lines { get; private set; } = Array.AsReadOnly(Array.Empty<LyricLine>());
    public string? LinesKey { get; private set; }
    public void InvalidateInFlight() => Interlocked.Increment(ref _generation);
    public bool IsCurrentGeneration(int generation) => Generation == generation;
    public int BeginManualSelection(string key)
    {
        InvalidateInFlight();
        _manualGenerations[key] = Generation;
        return Generation;
    }
    /// <summary>切走的自动结果仍可缓存，但不能越过该曲较新的手选世代。</summary>
    public bool MayCache(string key, int generation)
        => !_manualGenerations.TryGetValue(key, out var manual) || generation >= manual;
    public void Apply(IReadOnlyList<LyricLine> lines, string? key)
    {
        // 隔离加载器持有的列表；已发布的快照不能随下一次加载一起改变。
        Lines = Array.AsReadOnly(lines.ToArray());
        LinesKey = key;
    }
    public void Clear() => Apply([], null);
}
