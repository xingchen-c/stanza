using System.Diagnostics;
using Stanza.Models;

namespace Stanza.Services.Lyrics;

/// <summary>
///     歌词服务门面：按顺序尝试各歌词源，缓存当前歌曲的歌词行，并按播放位置取当前行。
/// </summary>
public sealed class LyricsService
{
    /// <summary>内存缓存上限：满时淘汰最久未用的（轻量 LRU，避免周期性全清导致命中率归零）</summary>
    private const int MaxMemoryCacheEntries = 20;

    // 最近歌曲的歌词缓存：切回听过的歌秒出，不用重新走网络搜索
    private readonly Dictionary<string, CacheEntry> _cache = new();
    private readonly ILyricsProvider[] _providers;
    private List<LyricLine> _lines = new();

    private sealed class CacheEntry
    {
        public LyricsLoadResult Status;
        public List<LyricLine> Lines = [];
        public long LastUsedUnixMs;
    }

    /// <summary>默认歌词源：QQ 音乐优先，LRCLIB 兜底</summary>
    public LyricsService()
        : this(new QqMusicLyricsProvider(), new LrclibLyricsProvider())
    {
    }

    /// <summary>可注入自定义歌词源（按顺序尝试，第一个返回 Loaded/Instrumental 的胜出）</summary>
    public LyricsService(params ILyricsProvider[] providers)
    {
        _providers = providers.Length > 0 ? providers : [new QqMusicLyricsProvider()];
    }

    /// <summary>当前歌曲的歌词行（按开始时间升序）</summary>
    public IReadOnlyList<LyricLine> Lines => _lines;

    /// <summary>搜索并加载歌词；返回加载结果</summary>
    public async Task<LyricsLoadResult> LoadAsync(string title, string artist, int? durationMs)
    {
        _lines = [];

        var cacheKey = TrackKey.Of(title, artist);
        if (_cache.TryGetValue(cacheKey, out var cached))
        {
            _lines = cached.Lines;
            cached.LastUsedUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            AppLog.Debug($"歌词缓存命中: '{title}' / {artist}（{cached.Status}）");
            return cached.Status;
        }

        // 磁盘缓存：重启后听过的歌秒出，QQ 接口挂起时也能兜底
        if (LyricsCacheService.TryGet(cacheKey, out var diskStatus, out var diskLines))
        {
            _lines = diskLines;
            PutCache(cacheKey, diskStatus, diskLines);
            AppLog.Debug($"歌词磁盘缓存命中: '{title}' / {artist}（{diskStatus}）");
            return diskStatus;
        }

        // 预算拆成"每源独立 + 总上限"：Lyricify 的 QQ 接口偶发挂起（默认 HttpClient 超时可达 100s）
        // 时，LRCLIB 兜底源仍能拿到自己的时间，而不是被 QQ 的 8 秒占满后直接放弃。
        var stopwatch = Stopwatch.StartNew();
        const double providerBudgetSec = 5.0; // 每个源最多等 5 秒
        const double totalBudgetSec = 8.0;    // 整条加载链最多 8 秒
        foreach (var provider in _providers)
        {
            AppLog.Debug($"尝试歌词源 {provider.Name}: '{title}' / {artist}");
            var remainingSec = Math.Min(providerBudgetSec, Math.Max(0, totalBudgetSec - stopwatch.Elapsed.TotalSeconds));
            if (remainingSec <= 0)
            {
                AppLog.Warn($"歌词源 {provider.Name} 已无剩余预算，本轮放弃");
                break;
            }

            LyricsResult result;
            try
            {
                var loadTask = provider.LoadAsync(title, artist, durationMs);
                var completed = await Task.WhenAny(loadTask, Task.Delay(TimeSpan.FromSeconds(remainingSec)));
                if (completed != loadTask)
                {
                    AppLog.Warn($"歌词源 {provider.Name} 超过 {remainingSec:F1}s 未返回，转下一个源");
                    // 超时后任务仍在后台跑（Lyricify API 不接受取消）：观察异常，避免 UnobservedTaskException
                    _ = loadTask.ContinueWith(t =>
                    {
                        if (t.IsFaulted)
                            AppLog.Warn($"歌词源 {provider.Name} 超时后异常: {t.Exception?.GetBaseException().Message}");
                    }, TaskScheduler.Default);
                    continue; // 该源超时：给下一个源（LRCLIB 兜底）剩余预算，而不是整条链直接放弃
                }

                result = await loadTask;
            }
            catch (Exception ex)
            {
                AppLog.Error($"歌词源 {provider.Name} 加载异常", ex);
                continue; // 单个源失败不影响其他源
            }

            if (result.Status == LyricsLoadResult.NotFound)
            {
                AppLog.Debug($"歌词源 {provider.Name} 未找到歌词");
                continue;
            }

            _lines = result.Lines.ToList();
            AppLog.Info($"歌词加载成功: 来源={provider.Name} 结果={result.Status} 行数={_lines.Count} 歌曲='{title}' / {artist}");

            // NotFound 不缓存（可能是网络抖动），Loaded/Instrumental 才缓存
            PutCache(cacheKey, result.Status, _lines);
            LyricsCacheService.Put(cacheKey, result.Status, _lines); // 持久化，重启不丢

            return result.Status;
        }

        AppLog.Warn($"所有歌词源均未找到歌词: '{title}' / {artist}");
        return LyricsLoadResult.NotFound;
    }

    /// <summary>写入内存缓存：满上限时淘汰最久未用的（比全清平滑）</summary>
    private void PutCache(string key, LyricsLoadResult status, IReadOnlyList<LyricLine> lines)
    {
        if (_cache.Count >= MaxMemoryCacheEntries)
        {
            var staleKey = _cache.OrderBy(kv => kv.Value.LastUsedUnixMs).First().Key;
            _cache.Remove(staleKey);
        }

        _cache[key] = new CacheEntry
        {
            Status = status,
            Lines = lines.ToList(),
            LastUsedUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };
    }

    /// <summary>清空歌词</summary>
    public void Clear()
    {
        _lines = new List<LyricLine>();
    }

    /// <summary>清除歌词缓存（内存 + 磁盘），排查歌词问题时用</summary>
    public void ClearCache()
    {
        _cache.Clear();
        LyricsCacheService.Clear();
        AppLog.Info("歌词缓存已清除（内存 + 磁盘）");
    }

    /// <summary>收集各源的搜索候选（手动选择歌词用）</summary>
    public async Task<IReadOnlyList<LyricCandidate>> GetCandidatesAsync(string title, string artist, int? durationMs)
    {
        var list = new List<LyricCandidate>();
        foreach (var provider in _providers)
            try
            {
                list.AddRange(await provider.GetCandidatesAsync(title, artist, durationMs));
            }
            catch (Exception ex)
            {
                AppLog.Error($"候选获取失败 {provider.Name}", ex);
            }

        return list;
    }

    /// <summary>按用户选中的候选直接取词（跳过自动匹配），并更新当前歌词行缓存</summary>
    public async Task<LyricsLoadResult> LoadCandidateAsync(LyricCandidate candidate)
    {
        var provider = _providers.FirstOrDefault(p => p.Key == candidate.SourceKey);
        if (provider is null)
            return LyricsLoadResult.NotFound;

        try
        {
            var result = await provider.LoadCandidateAsync(candidate);
            if (result.Status != LyricsLoadResult.NotFound)
                _lines = result.Lines.ToList();
            return result.Status;
        }
        catch (Exception ex)
        {
            AppLog.Error($"按候选取词失败 {provider.Name}", ex);
            return LyricsLoadResult.NotFound;
        }
    }

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
