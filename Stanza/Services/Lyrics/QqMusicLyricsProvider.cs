using Stanza.Models;
using Lyricify.Lyrics.Helpers;
using Lyricify.Lyrics.Models;
using Lyricify.Lyrics.Searchers;

namespace Stanza.Services.Lyrics;

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
    internal const double ArtistMatchThreshold = 0.6; // 完全相等或至少一位歌手交集

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
    public async Task<LyricsResult> LoadAsync(LyricsQuery query)
    {
        var title = query.Title;
        var artist = query.Artist;
        var durationMs = query.DurationMs;
        if (string.IsNullOrWhiteSpace(title))
            return new LyricsResult(LyricsLoadResult.NotFound, []);

        try
        {
            // 1. 用歌曲信息在 QQ 音乐搜索，拿到候选列表；完整标题搜不到时用清洗标题兜底
            var results = await SearchWithTitleFallbackAsync(title, artist, durationMs, query.Album);
            if (results.Count == 0)
                return new LyricsResult(LyricsLoadResult.NotFound, []);

            // 2. 多维综合打分排序后逐个取词：歌名无关的硬门槛提前过滤；
            //    跳过无歌词（纯音乐）的版本，直到找到带歌词的正确版本。
            //    全部无歌词 → 返回 Instrumental（确认真纯音乐）。
            //    排序分两档：先按「歌手精确度」降序（同名不同歌手时，精确歌手恒优先，
            //    避免错歌手凭时长凑巧接近反超），同档内再按综合分降序选版本，
            //    最后保持搜索原顺序避免抖动。
            var selection = PrepareAutoCandidates(results, query);
            var ranked = selection.Ranked;
            if (selection.UsedFallback && ranked.Count > 0)
            {
                AppLog.Info(
                    $"QQ 无歌手匹配候选，降级为歌名匹配（可能跨歌手）: '{title}' / '{artist}'，" +
                    $"歌名相关候选={ranked.Count}");
            }

            // 诊断：匹配输入 Info 落盘（Release 可见）；候选分布 Debug（调试器/DEBUG 包可见，
            // 偶发匹配错时切 Debug 打包或调试器里看完整候选分布）
            AppLog.Info($"QQ 匹配输入: '{title}' / '{artist}' / 时长={durationMs?.ToString() ?? "?"}ms，通过硬门槛候选数={ranked.Count}");
            for (var i = 0; i < Math.Min(ranked.Count, 10); i++)
            {
                var c = ranked[i];
                AppLog.Debug($"QQ 候选[{i + 1}] 分={c.Score:F2} '{c.Candidate.Title}' / {string.Join(", ", c.Candidate.Artists)} / 专辑='{c.Candidate.Album}' / 时长={c.Candidate.DurationMs?.ToString() ?? "?"}ms / MatchType={c.Candidate.MatchType}");
            }

            var sawInstrumental = false;
            var attempts = 0;
            foreach (var item in ranked)
            {
                if (++attempts > MaxAutoAttempts)
                    break;

                var qq = item.Candidate;
                AppLog.Debug($"QQ 候选尝试 {attempts}/{MaxAutoAttempts}: '{qq.Title}' / 分={item.Score:F2}");

                var result = await LoadBySongIdAsync(qq.Id, qq.Title ?? title);
                if (result.Status == LyricsLoadResult.Loaded)
                {
                    AppLog.Info($"QQ 命中: '{qq.Title}' / {string.Join(", ", qq.Artists)} / 分={item.Score:F2}");
                    return result; // 找到带歌词的版本
                }

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

    internal sealed record RankedCandidate(QQMusicSearchResult Candidate, int Index, double Score, double ArtistRank);

    /// <summary>自动候选先按歌名准入，再保留歌手优先级与无匹配歌手时的降级通道。</summary>
    internal static (List<RankedCandidate> Ranked, bool UsedFallback) PrepareAutoCandidates(
        IReadOnlyList<QQMusicSearchResult> results, LyricsQuery query)
    {
        var scored = results
            .Where(r => IsSameSongForAutoMatch(r.Title ?? query.Title, query.Title))
            .Select((r, index) => new RankedCandidate(r, index, ComputeMatchScore(
                r.Title, r.Artists, r.Album, r.DurationMs, r.MatchType,
                query.Title, query.Artist, query.DurationMs, query.Album),
                ArtistRankKey(r.Artists, query.Artist)))
            .OrderByDescending(x => x.ArtistRank)
            .ThenByDescending(x => x.Score)
            .ThenBy(x => x.Index)
            .ToList();
        return SelectByArtistTier(scored, x => x.ArtistRank, !string.IsNullOrWhiteSpace(query.Artist));
    }

    /// <summary>
    ///     匹配度排序权重：沿用库的百分值，Perfect 最高，Unknown / NoMatch 最低。
    ///     按 Lyricify.Lyrics.Helper 0.2.0 的 CompareHelper.MatchType 枚举名匹配；
    ///     升级库时若枚举改名，这里会静默落到 Unknown（0），需同步更新。
    /// </summary>
    internal static int MatchTypeRank(object? matchType)
    {
        return matchType?.ToString() switch
        {
            "Perfect" => 100,
            "VeryHigh" => 99,
            "High" => 95,
            "PrettyHigh" => 90,
            "Medium" => 70,
            "Low" => 30,
            "VeryLow" => 10,
            _ => 0
        };
    }

    /// <summary>歌名相似度 [0,1]：完全相等 1.0；一方包含 0.6+0.4×min/max；无关 0</summary>
    internal static double TitleScore(string? candidateTitle, string expectedTitle)
    {
        if (IsSameSongTitle(candidateTitle ?? string.Empty, expectedTitle))
            return 1.0;
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

    /// <summary>
    ///     自动匹配的歌手分档选择：有匹配候选时只用匹配项；一个都没有时降级使用全部歌名相关候选。
    ///     排序由调用方提前完成，本方法只决定范围，不改变顺序。
    /// </summary>
    internal static (List<T> Ranked, bool UsedFallback) SelectByArtistTier<T>(
        List<T> scored,
        Func<T, double> artistRank,
        bool hasExpectedArtist)
    {
        if (!hasExpectedArtist)
            return (scored, false);

        var matched = scored
            .Where(x => artistRank(x) >= ArtistMatchThreshold)
            .ToList();
        return matched.Count > 0
            ? (matched, false)
            : (scored, true);
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
        return MatchTypeRank(matchType) / 100.0;
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
    ///     纯函数、不依赖库类型入参，便于单测；缺失维度自动排除。
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
            return PrepareManualCandidates(results, query);
        }
        catch (Exception ex)
        {
            AppLog.Error($"QQ 搜索候选失败: {ex.Message}");
            return [];
        }
    }

    /// <summary>手动选择是纠正通道：只排序、不过滤，即使歌名或歌手不匹配也保留。</summary>
    internal IReadOnlyList<LyricCandidate> PrepareManualCandidates(
        IReadOnlyList<QQMusicSearchResult> results, LyricsQuery query)
    {
        var hasExpectedArtist = !string.IsNullOrWhiteSpace(query.Artist);
        return results
            .Select((r, index) => new
            {
                Candidate = new LyricCandidate(
                    Key, Name, r.Id, r.Title ?? query.Title,
                    r.Artists is { Length: > 0 } ? string.Join(", ", r.Artists) : query.Artist,
                    r.Album, r.DurationMs, r.MatchType?.ToString())
                {
                    MatchScore = ComputeMatchScore(
                        r.Title, r.Artists, r.Album, r.DurationMs, r.MatchType,
                        query.Title, query.Artist, query.DurationMs, query.Album),
                    ArtistMatched = hasExpectedArtist
                        ? ArtistRankKey(r.Artists, query.Artist) >= ArtistMatchThreshold
                        : null
                },
                Index = index,
                ArtistRank = ArtistRankKey(r.Artists, query.Artist)
            })
            .OrderByDescending(x => x.ArtistRank)
            .ThenByDescending(x => x.Candidate.MatchScore)
            .ThenBy(x => x.Index)
            .Select(x => x.Candidate)
            .ToList();
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

    /// <summary>歌名相关性：一方包含另一方（忽略空白/大小写）即视为同一首；空视为不相关</summary>
    internal static bool TitlesRelated(string lyricsTitle, string expectedTitle)
    {
        var a = Normalize(lyricsTitle);
        var b = Normalize(expectedTitle);
        if (a.Length == 0 || b.Length == 0)
            return false;
        if (a == b)
            return true;

        // 单字包含会把「星」吸到「星炬不熄」、「花」吸到「碎花」等大量无关歌曲；
        // 单字只在完全相等时成立，双字及以上才允许包含匹配。
        return Math.Min(a.Length, b.Length) >= 2 && (a.Contains(b) || b.Contains(a));
    }

    /// <summary>自动匹配保留历史包含准入，并补上搜索清洗后同名的合法版本。</summary>
    internal static bool IsSameSongForAutoMatch(string lyricsTitle, string expectedTitle)
        => TitlesRelated(lyricsTitle, expectedTitle) || IsSameSongTitle(lyricsTitle, expectedTitle);

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
