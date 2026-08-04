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

        var cacheKey = $"{title}{artist}";
        if (_cache.TryGetValue(cacheKey, out var cached))
        {
            _lines = cached.Lines;
            return cached.Status;
        }

        foreach (var provider in _providers)
        {
            LyricsResult result;
            try
            {
                result = await provider.LoadAsync(title, artist, durationMs);
            }
            catch
            {
                continue; // 单个源失败不影响其他源
            }

            if (result.Status == LyricsLoadResult.NotFound)
                continue;

            _lines = result.Lines.ToList();

            // NotFound 不缓存（可能是网络抖动），Loaded/Instrumental 才缓存
            if (_cache.Count >= 20)
                _cache.Clear(); // 简单上限，防止无限增长
            _cache[cacheKey] = (result.Status, _lines);

            return result.Status;
        }

        return LyricsLoadResult.NotFound;
    }

    /// <summary>清空歌词</summary>
    public void Clear()
    {
        _lines = new List<LyricLine>();
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
