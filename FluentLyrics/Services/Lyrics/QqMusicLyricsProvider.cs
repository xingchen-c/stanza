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

    // ---- 综合打分权重（MATCH-SCORING）：缺失维度自动归一化重分配 ----
    private const double TitleWeight = 0.35;
    private const double DurationWeight = 0.25;
    private const double MatchTypeWeight = 0.20;
    private const double ArtistWeight = 0.15;
    private const double AlbumWeight = 0.05;
    private const double DurationTauMs = 8000; // 时长接近度衰减尺度（8s 处 0.5）

    public string Key => "QQ";

    public string Name => "QQ 音乐";

    /// <summary>
    ///     搜索候选：SMTC 标题常带 "(中文翻译)" 这类括号副标题，完整标题搜不到时，
    ///     去掉尾部括号副标题再搜一次作为兜底（QQ 搜索可能被脏标题卡住）。
    /// </summary>
    private static async Task<IReadOnlyList<QQMusicSearchResult>> SearchWithTitleFallbackAsync(
        string title, string artist, int? durationMs)
    {
        var variants = new List<string> { title };
        var cleaned = StripTrailingParenthetical(title);
        if (cleaned is not null && !string.Equals(cleaned, title, StringComparison.Ordinal))
            variants.Add(cleaned);

        foreach (var variant in variants)
        {
            try
            {
                var metadata = new TrackMetadata { Title = variant, Artist = artist, DurationMs = durationMs };
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

        return [];
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

    public async Task<LyricsResult> LoadAsync(string title, string artist, int? durationMs)
    {
        if (string.IsNullOrWhiteSpace(title))
            return new LyricsResult(LyricsLoadResult.NotFound, []);

        try
        {
            // 1. 用歌曲信息在 QQ 音乐搜索，拿到候选列表；完整标题搜不到时用清洗标题兜底
            var results = await SearchWithTitleFallbackAsync(title, artist, durationMs);
            if (results.Count == 0)
                return new LyricsResult(LyricsLoadResult.NotFound, []);

            // 2. 多维综合打分排序后逐个取词：歌名无关的硬门槛提前过滤；
            //    跳过无歌词（纯音乐）的版本，直到找到带歌词的正确版本。
            //    全部无歌词 → 返回 Instrumental（确认真纯音乐）。
            var ranked = results
                .Where(r => TitlesRelated(r.Title ?? title, title)) // 硬门槛：歌名完全无关的不进打分
                .Select((r, index) => (Candidate: r, Index: index, Score: ComputeMatchScore(
                    r.Title, r.Artists, r.Album, r.DurationMs, r.MatchType,
                    title, artist, durationMs)))
                .OrderByDescending(x => x.Score)
                .ThenBy(x => x.Index) // 同分保持搜索原顺序，避免排序抖动
                .ToList();

            // 诊断（Info 级落盘）：偶发匹配错时需要看到完整候选分布，定位正确候选排到第几
            AppLog.Info($"QQ 匹配输入: '{title}' / '{artist}' / 时长={durationMs?.ToString() ?? "?"}ms，通过硬门槛候选数={ranked.Count}");
            for (var i = 0; i < Math.Min(ranked.Count, 10); i++)
            {
                var c = ranked[i];
                AppLog.Info($"QQ 候选[{i + 1}] 分={c.Score:F2} '{c.Candidate.Title}' / {string.Join(", ", c.Candidate.Artists)} / 专辑='{c.Candidate.Album}' / 时长={c.Candidate.DurationMs?.ToString() ?? "?"}ms / MatchType={c.Candidate.MatchType}");
            }

            var sawInstrumental = false;
            var attempts = 0;
            foreach (var item in ranked)
            {
                if (++attempts > MaxAutoAttempts)
                    break;

                var qq = item.Candidate;
                AppLog.Info($"QQ 候选尝试 {attempts}/{MaxAutoAttempts}: '{qq.Title}' / 分={item.Score:F2}");

                var result = await LoadBySongIdAsync(qq.Id, qq.Title ?? title);
                if (result.Status == LyricsLoadResult.Loaded)
                    return result; // 找到带歌词的版本

                if (result.Status == LyricsLoadResult.Instrumental)
                {
                    sawInstrumental = true;
                    AppLog.Info($"候选 '{qq.Title}' 无歌词（纯音乐），试下一个版本");
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

    /// <summary>歌名相似度 [0,1]：完全相等 1.0；一方包含 0.6+0.4×min/max；无关 0</summary>
    internal static double TitleScore(string? candidateTitle, string expectedTitle)
    {
        var a = Normalize(candidateTitle ?? string.Empty);
        var b = Normalize(expectedTitle);
        if (a.Length == 0 || b.Length == 0)
            return 0;
        if (a == b)
            return 1.0;
        if (a.Contains(b) || b.Contains(a))
        {
            var min = Math.Min(a.Length, b.Length);
            var max = Math.Max(a.Length, b.Length);
            return 0.6 + 0.4 * (min / (double)max);
        }

        return 0;
    }

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
            .Split(['/', ',', '、'], StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .ToHashSet();
    }

    /// <summary>时长接近度 [0,1]：1/(1+(Δ/τ)²) 平滑衰减，不做硬截断；任一时长缺失 → null</summary>
    internal static double? DurationScore(int? candidateDurationMs, int? expectedDurationMs)
    {
        if (candidateDurationMs is not { } actual || expectedDurationMs is not { } expected)
            return null; // 维度缺失

        var delta = Math.Abs(actual - expected);
        return 1.0 / (1.0 + Math.Pow(delta / DurationTauMs, 2));
    }

    /// <summary>MatchType 归一化 [0,1]：Perfect=1.0 … Unknown=0（始终计入）</summary>
    internal static double MatchTypeScore(object? matchType)
    {
        return MatchTypeRank(matchType) / 5.0;
    }

    /// <summary>专辑匹配度；期望专辑为空 → null（维度缺失，归一化时不计入）</summary>
    internal static double? AlbumScore(string? candidateAlbum, string? expectedAlbum)
    {
        if (string.IsNullOrWhiteSpace(expectedAlbum))
            return null; // 维度缺失

        var a = Normalize(candidateAlbum ?? string.Empty);
        var b = Normalize(expectedAlbum);
        if (a.Length == 0 || b.Length == 0)
            return 0.3;
        return a == b || a.Contains(b) || b.Contains(a) ? 1.0 : 0.3;
    }

    /// <summary>
    ///     多维综合打分 [0,1]：只对未缺失维度加权求和并归一化，任一信号失真时其他维度能纠偏。
    ///     纯函数、不依赖库类型入参，便于单测；album 暂不传（缺失自动排除）。
    /// </summary>
    internal static double ComputeMatchScore(
        string? candidateTitle, string[]? candidateArtists, string? candidateAlbum,
        int? candidateDurationMs, object? matchType,
        string expectedTitle, string? expectedArtist,
        int? expectedDurationMs, string? expectedAlbum = null)
    {
        double sum = 0, weight = 0;

        sum += TitleWeight * TitleScore(candidateTitle, expectedTitle);
        weight += TitleWeight;

        if (ArtistScore(candidateArtists, expectedArtist) is { } artistScore)
        {
            sum += ArtistWeight * artistScore;
            weight += ArtistWeight;
        }

        if (DurationScore(candidateDurationMs, expectedDurationMs) is { } durationScore)
        {
            sum += DurationWeight * durationScore;
            weight += DurationWeight;
        }

        sum += MatchTypeWeight * MatchTypeScore(matchType);
        weight += MatchTypeWeight;

        if (AlbumScore(candidateAlbum, expectedAlbum) is { } albumScore)
        {
            sum += AlbumWeight * albumScore;
            weight += AlbumWeight;
        }

        return weight > 0 ? sum / weight : 0;
    }

    /// <summary>搜索候选列表（手动选择歌词用）：返回 QQ 搜索的多条结果</summary>
    public async Task<IReadOnlyList<LyricCandidate>> GetCandidatesAsync(string title, string artist, int? durationMs)
    {
        if (string.IsNullOrWhiteSpace(title))
            return [];

        try
        {
            var results = await SearchWithTitleFallbackAsync(title, artist, durationMs);
            return results
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
                        if (!LyricParsing.TryGetTiming(line, out var startTime, out var text, out var endTime))
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
