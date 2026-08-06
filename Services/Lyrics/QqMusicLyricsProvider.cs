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
                DurationMs = durationMs
            };

            var searchResult = await SearcherHelper.QQMusicSearcher.SearchForResult(metadata);
            if (searchResult is not QQMusicSearchResult qq)
                return new LyricsResult(LyricsLoadResult.NotFound, []);

            AppLog.Debug($"QQ 搜索命中: '{qq.Title}' / {string.Join(", ", qq.Artists ?? [])} / 专辑='{qq.Album}' / 时长={qq.DurationMs?.ToString() ?? "?"}ms / MatchType={qq.MatchType}");

            // 2. 用歌曲 Id 获取解密后的歌词文本
            var response = await ProviderHelper.QQMusicApi.GetLyricsAsync(qq.Id);
            var rawLyrics = response?.Lyrics;

            // 搜到了歌但歌词内容为空：QQ 音乐对纯音乐就是这种表现，按纯音乐处理
            if (string.IsNullOrWhiteSpace(rawLyrics))
            {
                AppLog.Debug($"歌词内容为空（歌曲 '{qq.Title}'），按纯音乐处理");
                return new LyricsResult(LyricsLoadResult.Instrumental, []);
            }

            // 3. 解析成带时间轴的行：绝大多数是 Qrc，个别歌曲返回 Lrc，两种都试
            var parsed = ParseTimedLines(rawLyrics);

            // 4. 解析翻译（Trans 与正词同时间轴，按开始时间对齐挂到对应行）
            var translationMap = BuildTranslationMap(response?.Trans);
            AppLog.Debug($"翻译原始长度: {response?.Trans?.Length ?? 0}，翻译行数: {translationMap.Count}");
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
                            : MatchByTime(l.StartTime, translationMap),
                        l.Words))
                    .ToList();
            }

            if (parsed.Count > 0)
                return new LyricsResult(LyricsLoadResult.Loaded, parsed);

            // 有返回内容但解析不出任何时间轴行：
            // 1) 内容带时间戳标记却解析失败 → 可能是未知格式，按 NotFound 交给兜底源（LRCLIB），不误判纯音乐；
            // 2) 否则多半是纯音乐占位文案 → 按纯音乐处理。
            if (LooksLikeTimedLyrics(rawLyrics))
            {
                AppLog.Debug($"歌词含时间戳但解析失败（未知格式），原文前 200 字: {TruncateForLog(rawLyrics)}");
                return new LyricsResult(LyricsLoadResult.NotFound, []);
            }

            AppLog.Debug($"无时间轴歌词（疑似纯音乐），服务端原文: {TruncateForLog(rawLyrics)}");
            return new LyricsResult(LyricsLoadResult.Instrumental, []);
        }
        catch (Exception ex)
        {
            AppLog.Error($"QQ 搜索/取词失败: {ex.Message}");
            return new LyricsResult(LyricsLoadResult.NotFound, []);
        }
    }

    /// <summary>把歌词原文按 Qrc/Lrc 两种格式尝试解析，返回带时间轴的行（已按开始时间排序）；解析不出则返回空列表</summary>
    private static List<LyricLine> ParseTimedLines(string rawLyrics)
    {
        var parsed = new List<LyricLine>();
        foreach (var rawType in new[] { LyricsRawTypes.Qrc, LyricsRawTypes.Lrc })
            try
            {
                var data = ParseHelper.ParseLyrics(rawLyrics, rawType);
                parsed = new List<LyricLine>();
                if (data?.Lines is not null)
                    foreach (var line in data.Lines)
                    {
                        if (!TryGetTiming(line, out var startTime, out var text, out var endTime))
                            continue;
                        parsed.Add(new LyricLine(startTime, text, endTime, words: BuildWords(line)));
                    }

                parsed.Sort((a, b) => a.StartTime.CompareTo(b.StartTime));
                if (parsed.Count > 0)
                    return parsed;
            }
            catch
            {
                // 该格式解析失败就试下一种
            }

        return parsed;
    }

    /// <summary>从 SyllableLineInfo 提取逐字时间戳；普通行（无逐字数据）返回 null</summary>
    private static List<LyricWord>? BuildWords(ILineInfo line)
    {
        if (line is not SyllableLineInfo syllable ||
            !syllable.IsSyllable ||
            syllable.Syllables is not { Count: > 0 } syllables)
            return null;

        var words = new List<LyricWord>(syllables.Count);
        foreach (var s in syllables)
        {
            if (string.IsNullOrEmpty(s.Text) || s.EndTime <= s.StartTime)
                continue;
            words.Add(new LyricWord(s.Text, s.StartTime, s.EndTime));
        }

        return words.Count > 0 ? words : null;
    }

    /// <summary>原文是否带时间戳标记（[mm:ss] 等），用于区分"解析失败"与"纯音乐占位文案"</summary>
    private static bool LooksLikeTimedLyrics(string rawLyrics)
    {
        foreach (var line in rawLyrics.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length < 4 || trimmed[0] != '[')
                continue;

            var close = trimmed.IndexOf(']');
            if (close <= 1)
                continue;

            var tag = trimmed[1..close];
            var parts = tag.Split(':');
            if (parts.Length >= 2 && int.TryParse(parts[0], out _))
                return true;
        }

        return false;
    }

    /// <summary>日志原文截断，避免刷屏</summary>
    private static string TruncateForLog(string text)
    {
        return text.Length <= 200 ? text : text[..200] + $"…（共 {text.Length} 字符）";
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
                    foreach (var line in data.Lines)
                        if (TryGetTiming(line, out var start, out var text, out _) &&
                            !string.IsNullOrWhiteSpace(text) &&
                            !IsPlaceholderTranslation(text) &&
                            !map.ContainsKey(start))
                            map[start] = text;
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

    /// <summary>QQ 翻译里的占位符（"//" 表示该行无翻译），不能当真实翻译挂到歌词行上</summary>
    private static bool IsPlaceholderTranslation(string text)
    {
        return string.Equals(text.Trim(), "//", StringComparison.Ordinal);
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