using Stanza.Models;
using Lyricify.Lyrics.Helpers;
using Lyricify.Lyrics.Models;
using Lyricify.Lyrics.Searchers;

namespace Stanza.Services.Lyrics;

/// <summary>LRCLIB 歌词源：搜索候选，按 ID 获取并解析同步歌词。</summary>
public sealed class LrclibLyricsProvider : ILyricsProvider
{
    /// <inheritdoc />
    public string Key => "LRCLIB";

    /// <inheritdoc />
    public string Name => "LRCLIB";

    /// <inheritdoc />
    public Task<LyricsResult> LoadAsync(LyricsQuery query) => new LyricsProviderChain(this).LoadAsync(query);

    /// <inheritdoc />
    public async Task<IReadOnlyList<LyricCandidate>> GetCandidatesAsync(LyricsQuery query)
    {
        try
        {
            var metadata = new TrackMetadata
            {
                Title = query.Title, Artist = query.Artist, Album = query.Album, DurationMs = query.DurationMs
            };
            var results = await SearcherHelper.LRCLIBSearcher.SearchForResults(metadata);
            return (results ?? []).OfType<LRCLIBSearchResult>().Select(r => new LyricCandidate(
                Key, Name, r.Id.ToString(), r.Title ?? string.Empty, string.Join(", ", r.Artists ?? []),
                r.Album, r.DurationMs, r.MatchType?.ToString())).ToArray();
        }
        catch (Exception ex)
        {
            AppLog.Warn($"LRCLIB 搜索失败: {ex.Message}");
            return [];
        }
    }

    /// <inheritdoc />
    public async Task<LyricsResult> LoadCandidateAsync(LyricCandidate candidate)
    {
        if (!int.TryParse(candidate.Id, out var id))
            return new LyricsResult(LyricsLoadResult.NotFound, []);
        try
        {
            var response = await ProviderHelper.LRCLIBApi.GetById(id);
            if (response is null)
                return new LyricsResult(LyricsLoadResult.NotFound, []);

            // LRCLIB 官方标记：纯音乐
            if (response.Instrumental)
                return new LyricsResult(LyricsLoadResult.Instrumental, []);

            var rawLyrics = response.SyncedLyrics;
            if (!string.IsNullOrWhiteSpace(rawLyrics))
            {
                var data = ParseHelper.ParseLyrics(rawLyrics, LyricsRawTypes.Lrc);
                var parsed = new List<LyricLine>();
                if (data?.Lines is not null)
                {
                    foreach (var line in data.Lines)
                    {
                        if (!LyricParsing.TryGetTiming(line, out var startTime, out var text, out var endTime))
                            continue;
                        parsed.Add(new LyricLine(startTime, text, endTime));
                    }

                    parsed.Sort((a, b) => a.StartTime.CompareTo(b.StartTime));
                }

                if (parsed.Count > 0)
                    return new LyricsResult(LyricsLoadResult.Loaded, parsed);
            }

            return new LyricsResult(LyricsLoadResult.NotFound, []);
        }
        catch
        {
            return new LyricsResult(LyricsLoadResult.NotFound, []);
        }
    }

}
