using Stanza.Models;

namespace Stanza.Services.Lyrics;

/// <summary>
///     歌词服务门面：统一候选决策、缓存当前歌曲的歌词行，并按播放位置取当前行。
/// </summary>
public sealed class LyricsService
{
    private readonly ILyricsCacheStore _cacheStore;
    private readonly LyricsMemoryCache _memoryCache = new();
    private readonly LyricsProviderChain _providerChain;
    private List<LyricLine> _lines = [];
    private int _generation;
    private string? _linesKey;
    private readonly Dictionary<string, int> _manualGenerations = [];
    private readonly SemaphoreSlim _cacheWriteGate = new(1, 1);

    /// <summary>注入持久化缓存与参与统一决策的歌词源。</summary>
    internal LyricsService(ILyricsCacheStore cacheStore, params ILyricsProvider[] providers)
    {
        _cacheStore = cacheStore;
        _providerChain = new LyricsProviderChain(providers);
    }

    /// <summary>当前歌曲的歌词行（按开始时间升序）</summary>
    public IReadOnlyList<LyricLine> Lines => _lines;

    /// <summary>当前世代号。调用方在一次逻辑加载开始时取一次并贯穿该次加载。</summary>
    public int Generation => Volatile.Read(ref _generation);

    /// <summary>作废所有在飞的加载；切歌或会话断开时调用。</summary>
    public void InvalidateInFlight() => Interlocked.Increment(ref _generation);

    /// <summary>指定世代是否仍为当前世代；用于防止迟到结果改动显示状态。</summary>
    internal bool IsCurrentGeneration(int generation)
        => Volatile.Read(ref _generation) == generation;

    /// <summary>手动纠错接管同曲旧请求，但不作废其它曲目迟到结果的缓存资格。</summary>
    internal int BeginManualSelection(string key)
    {
        InvalidateInFlight();
        var generation = Generation;
        _manualGenerations[key] = generation;
        return generation;
    }

    /// <summary>搜索并加载歌词；返回加载结果。</summary>
    public async Task<LyricsLoadResult> LoadAsync(
        LyricsQuery query, int generation, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // 新歌须清旧词；同曲重试失败时保留已有可用歌词。
        if (IsCurrentGeneration(generation) && _linesKey != query.Key)
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
            _lines = lines;
            _linesKey = query.Key;
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
                _lines = lines;
                _linesKey = trackKey ?? key;
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
            _lines = hit.Lines;
            _linesKey = trackKey ?? key;
        }
        return hit.Status;
    }

    private bool MayCache(string key, int generation)
        => !_manualGenerations.TryGetValue(key, out var manualGeneration) || generation >= manualGeneration;

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
    public void Clear()
    {
        _lines = [];
        _linesKey = null;
    }

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
            _lines = lines;
            _linesKey = key;
        }
        return result.Status;
    }

    /// <summary>按播放位置获取当前歌词行索引。</summary>
    public int GetLineIndexAt(int positionMs) => GetLineIndexAt(_lines, positionMs);

    /// <summary>
    ///     按播放位置（毫秒）二分查找当前行索引（纯函数，可单测）。
    ///     还没到第一行时返回 0（提前显示第一行）；没有歌词时返回 -1。
    /// </summary>
    internal static int GetLineIndexAt(IReadOnlyList<LyricLine> lines, int positionMs)
    {
        if (lines.Count == 0 || positionMs < 0)
            return -1;
        if (positionMs < lines[0].StartTime)
            return 0;

        int lo = 0, hi = lines.Count - 1, result = -1;
        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            if (lines[mid].StartTime <= positionMs)
            {
                result = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return result;
    }
}
