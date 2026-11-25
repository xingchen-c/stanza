using Stanza.Models;

namespace Stanza.Services.Lyrics;

internal interface ILyricsCacheStore
{
    void Initialize();
    bool TryGet(string key, out LyricsLoadResult status, out List<LyricLine> lines);
    void Put(string key, LyricsLoadResult status, IReadOnlyList<LyricLine> lines);
    void Clear();
    void Remove(string key);

    Task RemoveAsync(string key) => Task.Run(() => Remove(key));

    /// <summary>把同步实现挪到线程池，避免 SQLite IO 压在 WPF UI 线程上。</summary>
    Task<LyricsCacheHit?> GetAsync(string key)
        => Task.Run(() => TryGet(key, out var status, out var lines)
            ? new LyricsCacheHit(status, lines)
            : (LyricsCacheHit?)null);

    /// <summary>异步写入缓存。</summary>
    Task PutAsync(string key, LyricsLoadResult status, IReadOnlyList<LyricLine> lines)
        => Task.Run(() => Put(key, status, lines));

    Task PutDecisionAsync(string key, LyricsLoadResult status, IReadOnlyList<LyricLine> lines,
        LyricsDecisionEvidence decision) => PutAsync(key, status, lines);
}

/// <summary>一次缓存命中的内容。</summary>
internal readonly record struct LyricsCacheHit(LyricsLoadResult Status, List<LyricLine> Lines,
    LyricsDecisionEvidence? Decision = null);
