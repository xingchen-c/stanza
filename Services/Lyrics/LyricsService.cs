using System.Diagnostics;
using FluentLyrics.Models;

namespace FluentLyrics.Services.Lyrics;

/// <summary>
///     歌词服务门面：按顺序尝试各歌词源，缓存当前歌曲的歌词行，并按播放位置取当前行。
/// </summary>
public sealed class LyricsService
{
    // 最近歌曲的歌词缓存：切回听过的歌秒出，不用重新走网络搜索
    private readonly Dictionary<string, (LyricsLoadResult Status, List<LyricLine> Lines)> _cache = new();
    private readonly ILyricsProvider[] _providers;
    private List<LyricLine> _lines = new();

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

        var cacheKey = $"{title}\u0001{artist}";
        if (_cache.TryGetValue(cacheKey, out var cached))
        {
            _lines = cached.Lines;
            AppLog.Debug($"歌词缓存命中: '{title}' / {artist}（{cached.Status}）");
            return cached.Status;
        }

        // 磁盘缓存：重启后听过的歌秒出，QQ 接口挂起时也能兜底
        if (LyricsCacheService.TryGet(cacheKey, out var diskStatus, out var diskLines))
        {
            _lines = diskLines;
            if (_cache.Count >= 20)
                _cache.Clear();
            _cache[cacheKey] = (diskStatus, diskLines);
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
                    AppLog.Warn($"歌词源 {provider.Name} 超过 {remainingSec:F1}s 未返回，本轮放弃");
                    break; // 该源超时：本轮不再等待后续源，交给上层重试/补抓
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
            if (_cache.Count >= 20)
                _cache.Clear(); // 简单上限，防止无限增长
            _cache[cacheKey] = (result.Status, _lines);
            LyricsCacheService.Put(cacheKey, result.Status, _lines); // 持久化，重启不丢

            return result.Status;
        }

        AppLog.Warn($"所有歌词源均未找到歌词: '{title}' / {artist}");
        return LyricsLoadResult.NotFound;
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

    /// <summary>
    ///     按播放位置（毫秒）二分查找当前行索引。
    ///     还没到第一行时返回 0（提前显示第一行）；没有歌词时返回 -1。
    /// </summary>
    public int GetLineIndexAt(int positionMs)
    {
        var lines = _lines;
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
