using FluentLyrics.Models;
using Lyricify.Lyrics.Helpers;
using Lyricify.Lyrics.Models;
using Lyricify.Lyrics.Searchers;

namespace FluentLyrics.Services.Lyrics;

/// <summary>QQ 音乐歌词源：搜索 → 取 QRC → 解析成带时间轴的行</summary>
public sealed class QqMusicLyricsProvider : ILyricsProvider
{
    /// <summary>自动匹配最多尝试的候选数：避免同名多版本时逐个取词拖太久</summary>
    private const int MaxAutoAttempts = 4;

    public string Key => "QQ";

    public string Name => "QQ 音乐";

    public async Task<LyricsResult> LoadAsync(string title, string artist, int? durationMs)
    {
        if (string.IsNullOrWhiteSpace(title))
            return new LyricsResult(LyricsLoadResult.NotFound, []);

        try
        {
            // 1. 用歌曲信息在 QQ 音乐搜索，拿到候选列表
            var metadata = new TrackMetadata
            {
                Title = title,
                Artist = artist,
                DurationMs = durationMs
            };

            var results = await SearcherHelper.QQMusicSearcher.SearchForResults(metadata);
            if (results is null || results.Count == 0)
                return new LyricsResult(LyricsLoadResult.NotFound, []);

            // 2. 逐个尝试候选：跳过时长不匹配（旧时长误配）与无歌词（纯音乐）的版本，
            //    直到找到带歌词的正确版本。全部无歌词 → 返回 Instrumental（确认真纯音乐）。
            var sawInstrumental = false;
            var attempts = 0;
            // 按匹配度（Perfect 优先）排序，同级再按时长差近的优先：
            // 同名多版本时长接近时，高匹配度的候选更可能是用户正在听的那个版本。
            var ordered = results
                .OfType<QQMusicSearchResult>()
                .OrderByDescending(r => MatchTypeRank(r.MatchType))
                .ThenBy(r => DurationDeltaMs(r, durationMs));
            foreach (var qq in ordered)
            {
                if (++attempts > MaxAutoAttempts)
                    break;

                AppLog.Debug($"QQ 候选尝试 {attempts}: '{qq.Title}' / {string.Join(", ", qq.Artists)} / 专辑='{qq.Album}' / 时长={qq.DurationMs?.ToString() ?? "?"}ms / MatchType={qq.MatchType}");

                // 候选名比对（第一道防线，取词前）：候选的歌名与期望歌名完全无关
                // （如"伴宅日记"的搜索结果里混入同歌手的"狼人杀狂想曲"）→ 直接跳过，省一次取词。
                if (!TitlesRelated(qq.Title ?? title, title))
                {
                    AppLog.Debug($"候选歌名与当前歌曲不符，跳过: '{qq.Title}'");
                    continue;
                }

                // 时长校验只用于低匹配度候选：SMTC 的 EndTime 本身可能报错
                // （如"伴宅日记"实际 263s，SMTC 却报 210s，差 52s），高匹配度候选要信任匹配度直接尝试；
                // 只有 Medium 及以下的低匹配度 + 时长差 >15s，才判定为误配（旧时长/同名其他歌）。
                if (MatchTypeRank(qq.MatchType) < 3 &&
                    durationMs is { } expected &&
                    qq.DurationMs is { } actual &&
                    Math.Abs(actual - expected) > 15000)
                {
                    AppLog.Debug($"候选时长不匹配，跳过: 期望={expected}ms 实际={actual}ms 歌曲='{qq.Title}'");
                    continue;
                }

                var result = await LoadBySongIdAsync(qq.Id, qq.Title ?? title);
                if (result.Status == LyricsLoadResult.Loaded)
                    return result; // 找到带歌词的版本

                if (result.Status == LyricsLoadResult.Instrumental)
                {
                    sawInstrumental = true;
                    AppLog.Debug($"候选 '{qq.Title}' 无歌词（纯音乐），试下一个版本");
                    continue;
                }

                // NotFound（解析失败/接口抖动）：试下一个候选
            }

            return new LyricsResult(
                sawInstrumental ? LyricsLoadResult.Instrumental : LyricsLoadResult.NotFound,
                []);
        }
        catch (Exception ex)
        {
            AppLog.Error($"QQ 搜索/取词失败: {ex.Message}");
            return new LyricsResult(LyricsLoadResult.NotFound, []);
        }
    }

    /// <summary>
    ///     匹配度排序权重：Perfect 最高，Unknown 最低。
    ///     按 Lyricify.Lyrics.Helper 0.2.0 的 CompareHelper.MatchType 枚举名匹配；
    ///     升级库时若枚举改名，这里会静默落到 Unknown（0），需同步更新。
    /// </summary>
    internal static int MatchTypeRank(object? matchType)
    {
        return matchType?.ToString() switch
        {
            "Perfect" => 5,
            "PrettyHigh" => 4,
            "High" => 3,
            "Medium" => 2,
            "Low" => 1,
            _ => 0
        };
    }

    /// <summary>候选时长与期望时长的差值（毫秒）；缺时长按 0 处理（不参与排序区分）</summary>
    internal static int DurationDeltaMs(QQMusicSearchResult r, int? expected)
    {
        if (expected is null || r.DurationMs is not { } actual)
            return 0;
        return Math.Abs(actual - expected.Value);
    }

    /// <summary>搜索候选列表（手动选择歌词用）：返回 QQ 搜索的多条结果</summary>
    public async Task<IReadOnlyList<LyricCandidate>> GetCandidatesAsync(string title, string artist, int? durationMs)
    {
        if (string.IsNullOrWhiteSpace(title))
            return [];

        try
        {
            var metadata = new TrackMetadata { Title = title, Artist = artist, DurationMs = durationMs };
            var results = await SearcherHelper.QQMusicSearcher.SearchForResults(metadata);
            return (results ?? [])
                .OfType<QQMusicSearchResult>()
                .Select(r => new LyricCandidate(
                    Key,
                    Name,
                    r.Id,
                    r.Title ?? title,
                    r.Artists is { Length: > 0 } ? string.Join(", ", r.Artists) : artist,
                    r.Album,
                    r.DurationMs,
                    r.MatchType?.ToString()))
                .ToList();
        }
        catch (Exception ex)
        {
            AppLog.Error($"QQ 搜索候选失败: {ex.Message}");
            return [];
        }
    }

    /// <summary>按用户选中的候选直接取歌词（跳过搜索匹配）</summary>
    public Task<LyricsResult> LoadCandidateAsync(LyricCandidate candidate)
    {
        return LoadBySongIdAsync(candidate.Id, candidate.Title);
    }

    /// <summary>按歌曲 ID 获取并解析歌词（搜索命中/用户候选共用）</summary>
    private async Task<LyricsResult> LoadBySongIdAsync(string id, string titleForLog)
    {
        try
        {
            var response = await ProviderHelper.QQMusicApi.GetLyricsAsync(id);
            var rawLyrics = response?.Lyrics;

            // 搜到了歌但歌词内容为空：QQ 音乐对纯音乐就是这种表现，按纯音乐处理
            if (string.IsNullOrWhiteSpace(rawLyrics))
            {
                AppLog.Debug($"歌词内容为空（歌曲 '{titleForLog}'），按纯音乐处理");
                return new LyricsResult(LyricsLoadResult.Instrumental, []);
            }

            // 解析成带时间轴的行：绝大多数是 Qrc，个别歌曲返回 Lrc，两种都试
            var (parsed, _) = ParseTimedLines(rawLyrics);

            // 解析翻译（Trans 与正词同时间轴，按开始时间对齐挂到对应行）
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
            AppLog.Error($"QQ 取词失败: {ex.Message}");
            return new LyricsResult(LyricsLoadResult.NotFound, []);
        }
    }

    /// <summary>把歌词原文按 Qrc/Lrc 两种格式尝试解析；返回带时间轴的行（已按开始时间排序）+ 歌词元数据；解析不出则返回空列表</summary>
    private static (List<LyricLine> Lines, ITrackMetadata? Metadata) ParseTimedLines(string rawLyrics)
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
                    return (parsed, data?.TrackMetadata);
            }
            catch
            {
                // 该格式解析失败就试下一种
            }

        return (parsed, null);
    }

    /// <summary>歌名相关性：一方包含另一方（忽略空白/大小写）即视为同一首；空视为不相关</summary>
    internal static bool TitlesRelated(string lyricsTitle, string expectedTitle)
    {
        var a = Normalize(lyricsTitle);
        var b = Normalize(expectedTitle);
        return a.Length > 0 && b.Length > 0 && (a.Contains(b) || b.Contains(a));
    }

    /// <summary>规范化：去空白、转小写，便于包含判断</summary>
    internal static string Normalize(string s)
    {
        return string.Concat(s.Where(ch => !char.IsWhiteSpace(ch))).ToLowerInvariant();
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
    internal static bool LooksLikeTimedLyrics(string rawLyrics)
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
    internal static Dictionary<int, string> BuildTranslationMap(string? transRaw)
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
    internal static string? MatchByTime(int startTime, Dictionary<int, string> map)
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
