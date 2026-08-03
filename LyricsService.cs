using Lyricify.Lyrics.Helpers;
using Lyricify.Lyrics.Models;
using Lyricify.Lyrics.Searchers;

namespace FluentLyrics;

/// <summary>一首歌的歌词行：毫秒开始时间 + 文本</summary>
public sealed class LyricLine(int startTime, string text)
{
    public int StartTime { get; } = startTime;
    public string Text { get; } = text;
}

public class LyricsService
{
    private List<LyricLine> _lines = new();

    /// <summary>当前歌曲的歌词行（按开始时间升序）</summary>
    public IReadOnlyList<LyricLine> Lines => _lines;


    /// <summary>搜索并加载歌词；加载成功返回 true，失败/找不到返回 false</summary>
    public async Task<bool> LoadAsync(string title, string artist, int? durationMs)
    {
        _lines = new List<LyricLine>();

        if (string.IsNullOrWhiteSpace(title))
            return false;

        try
        {
            // 1. 用歌曲信息在 QQ 音乐搜索，拿到歌曲 Id
            var metadata = new TrackMetadata
            {
                Title = title,
                Artist = artist,
                DurationMs = durationMs,
            };

            var searchResult = await SearcherHelper.QQMusicSearcher.SearchForResult(metadata);
            if (searchResult is not QQMusicSearchResult qq)
                return false;

            // 2. 用歌曲 Id 获取解密后的 QRC 歌词文本
            var response = await ProviderHelper.QQMusicApi.GetLyricsAsync(qq.Id);
            var rawLyrics = response?.Lyrics;
            if (string.IsNullOrWhiteSpace(rawLyrics))
                return false;

            // 3. 解析成带时间轴的行（必须显式指定 Qrc 类型，见下方说明）
            var data = ParseHelper.ParseLyrics(rawLyrics, LyricsRawTypes.Qrc);

            var parsed = new List<LyricLine>();
            if (data?.Lines is not null)
            {
                foreach (var line in data.Lines)
                {
                    if (!TryGetTiming(line, out var startTime, out var text))
                        continue;
                    parsed.Add(new LyricLine(startTime, text));
                }

                parsed.Sort((a, b) => a.StartTime.CompareTo(b.StartTime));
            }

            _lines = parsed;
            return _lines.Count > 0;
        }
        catch
        {
            _lines = new List<LyricLine>();
            return false;
        }
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

    /// <summary>安全读取一行的开始时间与文本（跳过空行、纯伴奏行等异常行）</summary>
    private static bool TryGetTiming(ILineInfo line, out int startTime, out string text)
    {
        startTime = 0;
        text = string.Empty;
        try
        {
            if (!line.StartTime.HasValue)
                return false;
            text = line.Text;
            if (string.IsNullOrWhiteSpace(text))
                return false;
            startTime = line.StartTime.Value;
            return true;
        }
        catch
        {
            return false;
        }
    }
}