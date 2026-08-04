using System.Diagnostics;
using FluentLyrics.Models;
using Lyricify.Lyrics.Helpers;
using Lyricify.Lyrics.Models;
using Lyricify.Lyrics.Searchers;

namespace FluentLyrics.Services.Lyrics;

/// <summary>QQ 音乐歌词源：搜索 → 取 QRC → 解析成带时间轴的行</summary>
public sealed class QqMusicLyricsProvider : ILyricsProvider
{
    public string Name => "QQ 音乐";

    public async Task<LyricsResult> LoadAsync(string title, string artist, int? durationMs)
    {
        if (string.IsNullOrWhiteSpace(title))
            return new LyricsResult(LyricsLoadResult.NotFound, []);

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
                return new LyricsResult(LyricsLoadResult.NotFound, []);

            // 2. 用歌曲 Id 获取解密后的 QRC 歌词文本
            var response = await ProviderHelper.QQMusicApi.GetLyricsAsync(qq.Id);
            var rawLyrics = response?.Lyrics;

            // 搜到了歌但歌词内容为空：QQ 音乐对纯音乐就是这种表现，按纯音乐处理
            if (string.IsNullOrWhiteSpace(rawLyrics))
            {
                Debug.WriteLine("[FluentLyrics] 歌词内容为空，按纯音乐处理");
                return new LyricsResult(LyricsLoadResult.Instrumental, []);
            }

            // 3. 解析成带时间轴的行（必须显式指定 Qrc 类型）
            var data = ParseHelper.ParseLyrics(rawLyrics, LyricsRawTypes.Qrc);

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

            // 4. 解析翻译（Trans 与正词同时间轴，按开始时间对齐挂到对应行）
            var translationMap = BuildTranslationMap(response?.Trans);
            Debug.WriteLine($"[FluentLyrics] 翻译原始长度: {response?.Trans?.Length ?? 0}，翻译行数: {translationMap.Count}");
            if (translationMap.Count > 0)
            {
                var transLines = translationMap.OrderBy(kv => kv.Key).Select(kv => kv.Value).ToList();
                parsed = parsed
                    .Select((l, i) => new LyricLine(
                        l.StartTime,
                        l.Text,
                        l.EndTime,
                        // 译文通常与正词逐行 1:1 对应；行数不一致时退回时间容差匹配
                        transLines.Count == parsed.Count
                            ? transLines[i]
                            : MatchByTime(l.StartTime, translationMap)))
                    .ToList();
            }

            if (parsed.Count > 0)
                return new LyricsResult(LyricsLoadResult.Loaded, parsed);

            // 有返回内容但解析不出任何时间轴行：多半是纯音乐占位文案，打日志确认服务端原文
            Debug.WriteLine($"[FluentLyrics] 无时间轴歌词（疑似纯音乐），服务端原文: {rawLyrics}");
            return new LyricsResult(LyricsLoadResult.Instrumental, []);
        }
        catch
        {
            return new LyricsResult(LyricsLoadResult.NotFound, []);
        }
    }

    /// <summary>安全读取一行的开始时间与文本（跳过空行、纯伴奏行等异常行）</summary>
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

    /// <summary>解析翻译文本（可能是 QRC 逐字或 LRC 逐行，两种都试），返回"开始时间 → 翻译"映射</summary>
    private static Dictionary<int, string> BuildTranslationMap(string? transRaw)
    {
        var map = new Dictionary<int, string>();
        if (string.IsNullOrWhiteSpace(transRaw))
            return map;

        foreach (var rawType in new[] { LyricsRawTypes.Qrc, LyricsRawTypes.Lrc })
        {
            try
            {
                var data = ParseHelper.ParseLyrics(transRaw, rawType);
                if (data?.Lines is not null)
                {
                    foreach (var line in data.Lines)
                    {
                        if (TryGetTiming(line, out var start, out var text, out _) &&
                            !string.IsNullOrWhiteSpace(text) &&
                            !map.ContainsKey(start))
                        {
                            map[start] = text;
                        }
                    }
                }
            }
            catch
            {
                // 某种格式解析失败就试下一种
            }

            if (map.Count > 0)
                break;
        }

        return map;
    }

    /// <summary>按开始时间匹配翻译：先精确，再 ±300ms 内取最近</summary>
    private static string? MatchByTime(int startTime, Dictionary<int, string> map)
    {
        if (map.TryGetValue(startTime, out var exact))
            return exact;

        string? best = null;
        var bestDelta = int.MaxValue;
        foreach (var kv in map)
        {
            var delta = Math.Abs(kv.Key - startTime);
            if (delta <= 300 && delta < bestDelta)
            {
                best = kv.Value;
                bestDelta = delta;
            }
        }

        return best;
    }
}
