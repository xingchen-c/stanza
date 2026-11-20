using Stanza.Models;
using Lyricify.Lyrics.Helpers;
using Lyricify.Lyrics.Models;
using Lyricify.Lyrics.Searchers;

namespace Stanza.Services.Lyrics;

/// <summary>QQ 音乐歌词源：搜索 → 取 QRC → 解析成带时间轴的行</summary>
public sealed class QqMusicLyricsProvider : ILyricsProvider
{
    /// <inheritdoc />
    public string Key => "QQ";

    /// <inheritdoc />
    public string Name => "QQ 音乐";

    /// <summary>
    ///     搜索候选：SMTC 标题常带「【前缀】」「(版本后缀)」等脏标记，完整标题搜不到时
    ///     逐级去掉再搜（如【盗墓笔记】见闻山海 (剧情版) → 见闻山海），避免 QQ 搜索被卡住。
    /// </summary>
    private static async Task<IReadOnlyList<QQMusicSearchResult>> SearchWithTitleFallbackAsync(
        string title, string artist, int? durationMs, string? album)
    {
        var variants = new List<string> { title };
        AddIfDistinct(variants, StripTrailingParenthetical(title));
        AddIfDistinct(variants, StripLeadingBracket(title));
        // 同时去掉前后缀（如【盗墓笔记】见闻山海 (剧情版) → 见闻山海）
        AddIfDistinct(variants, StripTrailingParenthetical(StripLeadingBracket(title) ?? title));

        // 群星类歌曲（几十位歌手）整串歌手名会拖垮搜索，只取前两位作搜索提示；
        // 完整歌手串仍用于后续综合打分（按分隔符求交集），不影响匹配准确率。
        var searchArtist = ShortenArtists(artist, 2);

        foreach (var variant in variants)
        {
            try
            {
                var metadata = new TrackMetadata { Title = variant, Artist = searchArtist, DurationMs = durationMs, Album = album };
                var results = await SearcherHelper.QQMusicSearcher.SearchForResults(metadata);
                var candidates = (results ?? []).OfType<QQMusicSearchResult>().ToList();
                if (candidates.Count > 0)
                {
                    if (!string.Equals(variant, title, StringComparison.Ordinal))
                        AppLog.Info($"QQ 搜索兜底：'{title}' 搜不到，清洗标题 '{variant}' 找到 {candidates.Count} 个候选");
                    return candidates;
                }
            }
            catch (Exception ex)
            {
                AppLog.Warn($"QQ 搜索失败（标题='{variant}'）: {ex.Message}");
            }
        }

        // 所有变体都搜不到：打一条汇总，方便定位「脏标题 + 群星歌手」这类搜索被卡死的模式
        AppLog.Info($"QQ 搜索无结果: '{title}' 歌手长={artist?.Length ?? 0} 尝试标题=[{string.Join(" → ", variants)}]");
        return [];
    }

    /// <summary>向标题变体列表追加一个非空且不重复的清洗结果</summary>
    private static void AddIfDistinct(List<string> variants, string? cleaned)
    {
        if (cleaned is not null && !variants.Any(v => string.Equals(v, cleaned, StringComparison.Ordinal)))
            variants.Add(cleaned);
    }

    /// <summary>群星歌的歌手串可能几十位，搜索时只保留前 N 位（完整串仍用于打分）；短串原样返回</summary>
    internal static string? ShortenArtists(string? artist, int maxCount)
    {
        if (string.IsNullOrWhiteSpace(artist))
            return artist;

        var parts = artist
            .Split(ArtistSeparators, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .ToArray();
        return parts.Length <= maxCount ? artist : string.Join("/", parts.Take(maxCount));
    }

    /// <summary>去掉标题尾部的括号副标题（半角/全角圆括号），返回清洗后的标题；无括号或只剩空白返回 null</summary>
    private static string? StripTrailingParenthetical(string title)
    {
        var trimmed = title.Trim();
        if (trimmed.Length < 2 || trimmed[^1] is not (')' or '）'))
            return null;

        // 从末尾右括号往前找最近的左括号，把这一对括号及其内容去掉
        for (var i = trimmed.Length - 2; i >= 0; i--)
        {
            if (trimmed[i] is '(' or '（')
            {
                var head = trimmed[..i].TrimEnd();
                return head.Length > 0 ? head : null;
            }
        }

        return null;
    }

    /// <summary>去掉标题开头的方括号前缀（【...】/[...]，如【盗墓笔记】见闻山海），返回核心歌名；无前缀返回 null</summary>
    internal static string? StripLeadingBracket(string title)
    {
        var trimmed = title.Trim();
        if (trimmed.Length < 3 || trimmed[0] is not ('【' or '['))
            return null;

        var close = trimmed[0] == '【' ? '】' : ']';
        var closeIdx = trimmed.IndexOf(close, 1);
        if (closeIdx < 0)
            return null;

        var tail = trimmed[(closeIdx + 1)..].TrimStart();
        return tail.Length > 0 ? tail : null;
    }

    /// <inheritdoc />
    public Task<LyricsResult> LoadAsync(LyricsQuery query) => new LyricsProviderChain(this).LoadAsync(query);

    /// <summary>歌手匹配度 [0,1]；期望歌手为空 → null（维度缺失，归一化时不计入）</summary>
    internal static double? ArtistScore(string[]? candidateArtists, string? expectedArtist)
    {
        if (string.IsNullOrWhiteSpace(expectedArtist))
            return null; // 维度缺失

        var candidate = string.Join(",", candidateArtists ?? []);
        var a = Normalize(candidate);
        var b = Normalize(expectedArtist);
        if (a.Length == 0 || b.Length == 0)
            return 0.2;
        if (a == b || a.Contains(b) || b.Contains(a))
            return 1.0;

        // 多歌手（"李凯年/李琳"）按分隔符拆开：集合完全相等（顺序/分隔符不同）→ 1.0，部分交集 → 0.6
        var aParts = SplitArtists(a);
        var bParts = SplitArtists(b);
        if (aParts.SetEquals(bParts))
            return 1.0;
        return aParts.Overlaps(bParts) ? 0.6 : 0.2;
    }

    private static HashSet<string> SplitArtists(string normalized)
    {
        return normalized
            .Split(ArtistSeparators, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .ToHashSet();
    }

    private static readonly char[] ArtistSeparators = ['/', ',', '，', '、', '&', ';', '；'];

    /// <summary>候选排序主键：歌手精确度降序值（精确 1.0 &gt; 部分交集 0.6 &gt; 无交集 0.2 &gt; 缺失 0）。
    ///     让「同名不同歌手」的错歌手不会因时长凑巧接近而反超精确歌手。</summary>
    internal static double ArtistRankKey(string[]? candidateArtists, string? expectedArtist)
        => ArtistScore(candidateArtists, expectedArtist) ?? 0;

    /// <summary>搜索候选列表（手动选择歌词用）：返回 QQ 搜索的多条结果</summary>
    public async Task<IReadOnlyList<LyricCandidate>> GetCandidatesAsync(LyricsQuery query)
    {
        var title = query.Title;
        var artist = query.Artist;
        var durationMs = query.DurationMs;
        if (string.IsNullOrWhiteSpace(title))
            return [];

        try
        {
            var results = await SearchWithTitleFallbackAsync(title, artist, durationMs, query.Album);
            return ToCandidates(results);
        }
        catch (Exception ex)
        {
            AppLog.Error($"QQ 搜索候选失败: {ex.Message}");
            return [];
        }
    }

    /// <summary>保留搜索原始元数据，交给跨源排序器统一比较。</summary>
    internal IReadOnlyList<LyricCandidate> ToCandidates(IReadOnlyList<QQMusicSearchResult> results)
    {
        return results.Select(r => new LyricCandidate(
            Key, Name, r.Id, r.Title ?? string.Empty, string.Join(", ", r.Artists ?? []),
            r.Album, r.DurationMs, r.MatchType?.ToString())).ToArray();
    }

    /// <summary>按用户选中的候选直接取歌词（跳过搜索匹配）</summary>
    public Task<LyricsResult> LoadCandidateAsync(LyricCandidate candidate)
    {
        return LoadBySongIdAsync(candidate.Id, candidate.Title);
    }

    /// <summary>按歌曲 ID 获取并解析歌词（搜索命中/用户候选共用）</summary>
    private static async Task<LyricsResult> LoadBySongIdAsync(string id, string titleForLog)
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
                        if (!LyricParsing.TryGetTiming(line, out var startTime, out var text, out var endTime))
                            continue;
                        parsed.Add(new LyricLine(startTime, text, endTime, words: LyricParsing.BuildWords(line)));
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

    /// <summary>手动列表的相关性只认可规范核心歌名一致，不能把包含歌名的其它歌曲当作同曲。</summary>
    internal static bool IsSameSongTitle(string candidateTitle, string expectedTitle)
    {
        var candidate = NormalizeCoreTitle(candidateTitle);
        var expected = NormalizeCoreTitle(expectedTitle);
        return candidate.Length > 0 && expected.Length > 0 && candidate == expected;
    }

    private static string NormalizeCoreTitle(string title)
    {
        var core = StripLeadingBracket(title) ?? title.Trim();
        // 搜索可去掉任意括号以增加召回，但判定同曲只能去掉明确版本；朗诵、诗词作者等副标题不能忽略。
        var open = core.LastIndexOfAny(['(', '（']);
        if (open > 0 && core[^1] is (')' or '）') && IsVersionSuffix(core[(open + 1)..^1]))
            core = core[..open].TrimEnd();
        // 只清理明确分隔的版本后缀，不截断正常歌名中的连字符或任意副标题。
        foreach (var separator in new[] { " - ", " – ", " — " })
        {
            var index = core.LastIndexOf(separator, StringComparison.Ordinal);
            if (index <= 0)
                continue;
            if (IsVersionSuffix(core[(index + separator.Length)..]))
            {
                core = core[..index];
                break;
            }
        }
        return Normalize(core);
    }

    private static bool IsVersionSuffix(string suffix)
        => Normalize(suffix) is "live" or "liveversion" or "live版" or "remix" or "remix版"
            or "伴奏" or "伴奏版" or "现场版" or "剧情版";

    /// <summary>规范化：去空白、转小写，便于包含判断</summary>
    internal static string Normalize(string s)
    {
        return string.Concat(s.Where(ch => !char.IsWhiteSpace(ch))).ToLowerInvariant();
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
                        if (LyricParsing.TryGetTiming(line, out var start, out var text, out _) &&
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
