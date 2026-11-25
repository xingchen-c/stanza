using Stanza.Core.Models;

namespace Stanza.Core.Lyrics;

/// <summary>
///     歌词加载与缓存写入流程；当前行与世代由 LyricsContext 唯一持有。
/// </summary>
internal sealed class LyricsLoader
{
    private readonly ILyricsCacheStore _cacheStore;
    private readonly LyricsMemoryCache _memoryCache = new();
    private readonly LyricsProviderChain _providerChain;
    internal LyricsContext Context { get; }
    private readonly SemaphoreSlim _cacheWriteGate = new(1, 1);

    /// <summary>注入持久化缓存与参与统一决策的歌词源。</summary>
    internal LyricsLoader(ILyricsCacheStore cacheStore, params ILyricsProvider[] providers)
        : this(new LyricsContext(), cacheStore, new LyricsProviderChain(providers)) { }

    internal LyricsLoader(LyricsContext context, ILyricsCacheStore cacheStore, LyricsProviderChain providerChain)
    {
        Context = context;
        _cacheStore = cacheStore;
        _providerChain = providerChain;
    }

    public IReadOnlyList<LyricLine> Lines => Context.Lines;
    public int Generation => Context.Generation;
    public void InvalidateInFlight() => Context.InvalidateInFlight();
    internal bool IsCurrentGeneration(int generation) => Context.IsCurrentGeneration(generation);
    internal int BeginManualSelection(string key) => Context.BeginManualSelection(key);

    /// <summary>搜索并加载歌词；返回加载结果。</summary>
    public async Task<LyricsLoadResult> LoadAsync(
        LyricsQuery query, int generation, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // 新歌须清旧词；同曲重试失败时保留已有可用歌词。
        if (IsCurrentGeneration(generation) && Context.LinesKey != query.Key)
            Clear();

        var cacheKey = AutomaticCacheKey(query);
        if (await TryLoadCachedAsync(cacheKey, generation, cancellationToken, query.Key) is { } cached)
            return cached;

        var result = await _providerChain.LoadAsync(query, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (result.Status is LyricsLoadResult.NotFound or LyricsLoadResult.Ambiguous)
            return result.Status;

        var lines = result.Lines.ToList();
        await CacheAsync(cacheKey, result.Status, lines, generation, manual: false, cancellationToken,
            query.Key, result.Decision);
        cancellationToken.ThrowIfCancellationRequested();
        if (IsCurrentGeneration(generation))
        {
            Context.Apply(lines, query.Key);
        }
        return result.Status;
    }

    internal async Task<LyricsLoadResult> ReloadAutomaticAsync(
        LyricsQuery query, int generation, CancellationToken cancellationToken)
    {
        var key = AutomaticCacheKey(query);
        // 删除排在旧写入之后；已接受的清除操作不随选择窗口关闭而撤销。
        await _cacheWriteGate.WaitAsync();
        try
        {
            await _cacheStore.RemoveAsync(key);
            _memoryCache.Remove(key);
        }
        finally
        {
            _cacheWriteGate.Release();
        }
        return await LoadAsync(query, generation, cancellationToken);
    }

    // 缓存内部身份，不使用会随会话变化的曲目 revision，也不与自动匹配缓存混用。
    internal static string CandidateCacheKey(string key, LyricCandidate candidate)
        => $"candidate:{key.Length}:{key}{candidate.SourceKey.Length}:{candidate.SourceKey}{candidate.Id}";

    internal static string AutomaticCacheKey(LyricsQuery query)
        => $"auto:{LyricsCandidateRanker.AlgorithmVersion}:{LyricsCandidateRanker.Fingerprint(query)}";

    internal async Task<LyricsLoadResult?> TryLoadCachedAsync(
        string key, int generation, CancellationToken cancellationToken = default, string? trackKey = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_memoryCache.TryGet(key, out var status, out var lines, out _))
        {
            if (IsCurrentGeneration(generation))
            {
                Context.Apply(lines, trackKey ?? key);
            }
            return status;
        }

        var cached = await _cacheStore.GetAsync(key).WaitAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (cached is not { } hit)
            return null;

        if (MayCache(trackKey ?? key, generation))
            _memoryCache.Put(key, hit.Status, hit.Lines, hit.Decision);
        if (IsCurrentGeneration(generation))
        {
            Context.Apply(hit.Lines, trackKey ?? key);
        }
        return hit.Status;
    }

    private bool MayCache(string key, int generation) => Context.MayCache(key, generation);

    private async Task CacheAsync(string key, LyricsLoadResult status, List<LyricLine> lines,
        int generation, bool manual, CancellationToken token, string? trackKey = null,
        LyricsDecisionEvidence? decision = null)
    {
        // 磁盘写入也保持调用顺序，防止已开始的旧自动写入在手选写入之后才完成。
        await _cacheWriteGate.WaitAsync(token);
        try
        {
            if (!MayCache(trackKey ?? key, generation) || (manual && !IsCurrentGeneration(generation)))
                return;
            token.ThrowIfCancellationRequested();
            if (decision is not null)
                await _cacheStore.PutDecisionAsync(key, status, lines, decision);
            else
                await _cacheStore.PutAsync(key, status, lines);
            token.ThrowIfCancellationRequested();
            if (MayCache(trackKey ?? key, generation) && (!manual || IsCurrentGeneration(generation)))
                _memoryCache.Put(key, status, lines, decision);
        }
        finally
        {
            _cacheWriteGate.Release();
        }
    }

    /// <summary>清空歌词</summary>
    public void Clear() => Context.Clear();

    /// <summary>清除歌词缓存（内存 + 磁盘），排查歌词问题时用</summary>
    public void ClearCache()
    {
        _memoryCache.Clear();
        _cacheStore.Clear();
        AppLog.Info("歌词缓存已清除（内存 + 磁盘）");
    }

    /// <summary>收集各源的搜索候选（手动选择歌词用）</summary>
    public Task<IReadOnlyList<LyricCandidate>> GetCandidatesAsync(
        LyricsQuery query, CancellationToken cancellationToken = default)
        => _providerChain.GetCandidatesAsync(query, cancellationToken);

    /// <summary>按用户选中的候选直接取词（跳过自动匹配），并更新当前歌词行缓存</summary>
    public async Task<LyricsLoadResult> LoadCandidateAsync(
        LyricCandidate candidate,
        int generation,
        string? key = null,
        CancellationToken cancellationToken = default)
    {
        var result = await _providerChain.LoadCandidateAsync(candidate, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (result.Status == LyricsLoadResult.NotFound || !IsCurrentGeneration(generation))
            return result.Status;

        var lines = result.Lines.ToList();
        // 纯音乐不记忆，避免一次误选永久挡住正常自动匹配。
        if (key is not null && result.Status != LyricsLoadResult.Instrumental)
        {
            await CacheAsync(CandidateCacheKey(key, candidate), result.Status, lines, generation, manual: true, cancellationToken);
            await CacheAsync(key, result.Status, lines, generation, manual: true, cancellationToken);
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (IsCurrentGeneration(generation))
        {
            Context.Apply(lines, key);
        }
        return result.Status;
    }

}
