using Lyricify.Lyrics.Helpers;
using Lyricify.Lyrics.Models;
using Lyricify.Lyrics.Searchers;
using Stanza.Models;

namespace Stanza.Services.Lyrics;

/// <summary>网易云音乐歌词源：搜索候选，按 ID 取 Yrc/Lrc 并解析时间轴（Yrc 含逐字）。</summary>
public sealed class NeteaseLyricsProvider : ILyricsProvider
{
    /// <inheritdoc />
    public string Key => "NetEase";

    /// <inheritdoc />
    public string Name => "网易云音乐";

    /// <inheritdoc />
    public Task<LyricsResult> LoadAsync(LyricsQuery query) => new LyricsProviderChain(this).LoadAsync(query);

    /// <inheritdoc />
    public async Task<IReadOnlyList<LyricCandidate>> GetCandidatesAsync(LyricsQuery query)
    {
        var title = query.Title;
        var artist = query.Artist;
        var durationMs = query.DurationMs;
        try
        {
            var results = await SearchAsync(title, artist, durationMs);
            return results.Select(r => new LyricCandidate(
                Key, Name, r.Id, r.Title ?? string.Empty, string.Join(", ", r.Artists ?? []),
                r.Album, r.DurationMs, r.MatchType?.ToString())).ToArray();
        }
        catch (Exception ex)
        {
            AppLog.Error($"网易云搜索候选失败: {ex.Message}");
            return [];
        }
    }

    /// <summary>按已选候选直接取歌词（自动与手动共用）</summary>
    public Task<LyricsResult> LoadCandidateAsync(LyricCandidate candidate)
    {
        return LoadByIdAsync(candidate.Id, candidate.Title);
    }

    /// <summary>网易云搜索：返回候选列表</summary>
    private static async Task<List<NeteaseSearchResult>> SearchAsync(string title, string artist, int? durationMs)
    {
        var metadata = new TrackMetadata { Title = title, Artist = artist, DurationMs = durationMs };
        var results = await SearcherHelper.NeteaseSearcher.SearchForResults(metadata);
        return (results ?? []).OfType<NeteaseSearchResult>().ToList();
    }

    /// <summary>按歌曲 ID 取词并解析：Yrc（逐字）优先，无 Yrc 回退 Lrc；翻译按时间对齐</summary>
    private static async Task<LyricsResult> LoadByIdAsync(string id, string titleForLog)
    {
        try
        {
            var lyric = await ProviderHelper.NeteaseApi.GetLyric(id);
            if (lyric is null)
                return new LyricsResult(LyricsLoadResult.NotFound, []);

            // 官方纯音乐标记
            if (lyric.Nolyric)
                return new LyricsResult(LyricsLoadResult.Instrumental, []);

            // 翻译：Tlyric（逐行）优先，Ytlrc（逐字）兜底；复用 QQ 的 LRC 翻译解析
            var translationMap = QqMusicLyricsProvider.BuildTranslationMap(
                lyric.Tlyric?.Lyric ?? lyric.Ytlrc?.Lyric);

            var parsed = ParseTimedLines(lyric.Yrc?.Lyric, lyric.Lrc?.Lyric, translationMap);
            if (parsed.Count > 0)
                return new LyricsResult(LyricsLoadResult.Loaded, parsed);

            // 未收录、空响应或无法解析都不能证明是纯音乐；只有上面的官方标记可以。
            return new LyricsResult(LyricsLoadResult.NotFound, []);
        }
        catch (Exception ex)
        {
            AppLog.Error($"网易云取词失败: {ex.Message}");
            return new LyricsResult(LyricsLoadResult.NotFound, []);
        }
    }

    /// <summary>解析网易云歌词：Yrc（逐字）优先，回退 Lrc；翻译按开始时间对齐挂到行上</summary>
    private static List<LyricLine> ParseTimedLines(
        string? yrcLyric, string? lrcLyric, Dictionary<int, string> translationMap)
    {
        if (!string.IsNullOrWhiteSpace(yrcLyric))
        {
            var lines = ParseRaw(yrcLyric, LyricsRawTypes.Yrc);
            if (lines.Count > 0)
                return AttachTranslation(lines, translationMap);
        }

        if (!string.IsNullOrWhiteSpace(lrcLyric))
        {
            var lines = ParseRaw(lrcLyric, LyricsRawTypes.Lrc);
            if (lines.Count > 0)
                return AttachTranslation(lines, translationMap);
        }

        return [];
    }

    /// <summary>按给定格式解析歌词原文，返回带时间轴的行（已排序）；解析失败返回空</summary>
    private static List<LyricLine> ParseRaw(string raw, LyricsRawTypes rawType)
    {
        var parsed = new List<LyricLine>();
        try
        {
            var data = ParseHelper.ParseLyrics(raw, rawType);
            if (data?.Lines is null)
                return parsed;

            foreach (var line in data.Lines)
            {
                if (!LyricParsing.TryGetTiming(line, out var startTime, out var text, out var endTime))
                    continue;
                parsed.Add(new LyricLine(startTime, text, endTime, words: LyricParsing.BuildWords(line)));
            }

            parsed.Sort((a, b) => a.StartTime.CompareTo(b.StartTime));
        }
        catch
        {
            // 该格式解析失败就回退另一种
        }

        return parsed;
    }

    /// <summary>翻译按时间对齐：行数 1:1 按序挂，否则按时间容差匹配（与 QQ 一致）</summary>
    private static List<LyricLine> AttachTranslation(List<LyricLine> lines, Dictionary<int, string> translationMap)
    {
        if (translationMap.Count == 0)
            return lines;

        var transLines = translationMap.OrderBy(kv => kv.Key).Select(kv => kv.Value).ToList();
        return lines.Select((l, i) => new LyricLine(
            l.StartTime,
            l.Text,
            l.EndTime,
            transLines.Count == lines.Count
                ? transLines[i]
                : QqMusicLyricsProvider.MatchByTime(l.StartTime, translationMap),
            l.Words)).ToList();
    }
}
