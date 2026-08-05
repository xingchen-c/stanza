using FluentLyrics.Models;

namespace FluentLyrics.Services.Lyrics;

/// <summary>
/// 歌词服务门面：按顺序尝试各歌词源，缓存当前歌曲的歌词行，并按播放位置取当前行。
/// </summary>
public sealed class LyricsService
{
    private readonly ILyricsProvider[] _providers;
    private List<LyricLine> _lines = new();

    // 最近歌曲的歌词缓存：切回听过的歌秒出，不用重新走网络搜索
    private readonly Dictionary<string, (LyricsLoadResult Status, List<LyricLine> Lines)> _cache = new();

    /// <summary>当前歌曲的歌词行（按开始时间升序）</summary>
    public IReadOnlyList<LyricLine> Lines => _lines;

    /// <summary>默认歌词源：QQ 音乐优先，LRCLIB 兜底</summary>
    public LyricsService()
        : this([new QqMusicLyricsProvider(), new LrclibLyricsProvider()])
    {
    }

    /// <summary>可注入自定义歌词源（按顺序尝试，第一个返回 Loaded/Instrumental 的胜出）</summary>
    public LyricsService(params ILyricsProvider[] providers)
    {
        _providers = providers.Length > 0 ? providers : [new QqMusicLyricsProvider()];
    }

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

        // 总预算 8 秒：Lyricify 的 QQ 接口偶发挂起（默认 HttpClient 超时可达 100s），
        // 不能让卡死的请求把整条切歌流程拖住，超时即视为"未找到"，交给上层重试。
        var deadline = Task.Delay(TimeSpan.FromSeconds(8));
        foreach (var provider in _providers)
        {
            AppLog.Debug($"尝试歌词源 {provider.Name}: '{title}' / {artist}");
            LyricsResult result;
            try
            {
                var loadTask = provider.LoadAsync(title, artist, durationMs);
                var completed = await Task.WhenAny(loadTask, deadline);
                if (completed != loadTask)
                {
                    AppLog.Warn($"歌词源 {provider.Name} 超过总预算 8s 未返回，本轮放弃");
                    break; // 总超时：该源（及后续源）本轮不再等待，直接返回 NotFound
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
    /// 按播放位置（毫秒）二分查找当前行索引。
    /// 还没到第一行时返回 0（提前显示第一行）；没有歌词时返回 -1。
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
            int mid = (lo + hi) / 2;
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
