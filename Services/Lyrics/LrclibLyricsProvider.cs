using FluentLyrics.Models;
using Lyricify.Lyrics.Helpers;
using Lyricify.Lyrics.Models;
using Lyricify.Lyrics.Searchers;

namespace FluentLyrics.Services.Lyrics;

/// <summary>LRCLIB 歌词源（兜底）：搜索 → 按 ID 取 LRC → 解析成带时间轴的行</summary>
public sealed class LrclibLyricsProvider : ILyricsProvider
{
    public string Name => "LRCLIB";

    public async Task<LyricsResult> LoadAsync(string title, string artist, int? durationMs)
    {
        if (string.IsNullOrWhiteSpace(title))
            return new LyricsResult(LyricsLoadResult.NotFound, []);

        try
        {
            // 1. 用歌曲信息在 LRCLIB 搜索，拿到歌曲 Id
            var metadata = new TrackMetadata
            {
                Title = title,
                Artist = artist,
                DurationMs = durationMs,
            };

            var searchResult = await SearcherHelper.LRCLIBSearcher.SearchForResult(metadata);
            if (searchResult is not LRCLIBSearchResult lrclib)
                return new LyricsResult(LyricsLoadResult.NotFound, []);

            // 2. 用歌曲 Id 获取歌词
            var response = await ProviderHelper.LRCLIBApi.GetById(lrclib.Id);
            if (response is null)
                return new LyricsResult(LyricsLoadResult.NotFound, []);

            // LRCLIB 官方标记：纯音乐
            if (response.Instrumental)
                return new LyricsResult(LyricsLoadResult.Instrumental, []);

            // 3. 优先解析同步歌词（LRC）
            var rawLyrics = response.SyncedLyrics;
            if (!string.IsNullOrWhiteSpace(rawLyrics))
            {
                var data = ParseHelper.ParseLyrics(rawLyrics, LyricsRawTypes.Lrc);
                var parsed = new List<LyricLine>();
                if (data?.Lines is not null)
                {
                    foreach (var line in data.Lines)
                    {
                        if (!TryGetTiming(line, out var startTime, out var text, out var endTime))
                            continue;
                        parsed.Add(new LyricLine(startTime, text, endTime));
                    }

                    parsed.Sort((a, b) => a.StartTime.CompareTo(b.StartTime));
                }

                if (parsed.Count > 0)
                    return new LyricsResult(LyricsLoadResult.Loaded, parsed);
            }

            // 没有同步歌词但有纯文本歌词 → 按无时间轴处理（显示占位文案）
            if (!string.IsNullOrWhiteSpace(response.PlainLyrics))
                return new LyricsResult(LyricsLoadResult.Instrumental, []);

            return new LyricsResult(LyricsLoadResult.NotFound, []);
        }
        catch
        {
            return new LyricsResult(LyricsLoadResult.NotFound, []);
        }
    }

    /// <summary>安全读取一行的开始/结束时间与文本（跳过空行等异常行）</summary>
    private static bool TryGetTiming(ILineInfo line, out int startTime, out string text, out int? endTime)
    {
        startTime = 0;
        text = string.Empty;
        endTime = null;
        try
        {
            if (!line.StartTime.HasValue)
                return false;
            text = line.Text;
            if (string.IsNullOrWhiteSpace(text))
                return false;
            startTime = line.StartTime.Value;
            endTime = line.EndTime;
            return true;
        }
        catch
        {
            return false;
        }
    }
}
