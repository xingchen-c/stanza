using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Lyricify.Lyrics.Models;
using Lyricify.Lyrics.Searchers;
using Lyricify.Lyrics.Searchers.Helpers;
using Stanza.Core.Models;

namespace Stanza.Core.Lyrics;

/// <summary>自动与手动共用的元数据比较；来源偏好不参与匹配分。</summary>
internal static class LyricsCandidateRanker
{
    internal const int AlgorithmVersion = 4;
    private static readonly char[] ArtistSeparators = ['/', ',', '，', '、', '&', ';', '；'];
    private static readonly Regex VersionTags = new(
        @"\blive(?:\s*version)?\b|现场版?|\bremix\b|混音版?|伴奏版?|纯音乐|\binstrumental\b|\boff\s*vocal\b|剧情版|\bremaster(?:ed)?\b(?:\s*\d{4})?",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    internal static string Fingerprint(LyricsQuery query)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(query))));

    internal static IReadOnlyList<LyricCandidate> Rank(IEnumerable<LyricCandidate> candidates, LyricsQuery query)
    {
        var ranked = candidates.DistinctBy(c => (c.SourceKey, c.Id))
            .Select(c => Score(c, query))
            .OrderByDescending(c => c.TitleMatched)
            .ThenByDescending(c => c.VersionMatched)
            .ThenByDescending(c => ArtistRank(c, query))
            .ThenByDescending(c => c.MatchScore)
            .ToList();

        // 只在同一库等级、歌手档及录音证据相同的小组内调整来源顺序，避免非传递的分差比较器。
        for (var start = 0; start < ranked.Count;)
        {
            var first = ranked[start];
            var end = start + 1;
            while (end < ranked.Count && ranked[end].TitleMatched == first.TitleMatched &&
                   ranked[end].VersionMatched == first.VersionMatched &&
                   ArtistRank(ranked[end], query) == ArtistRank(first, query) &&
                   ConfidenceBand(ranked[end]) == ConfidenceBand(first) && SameRecording(first, ranked[end]))
                end++;
            var group = ranked.GetRange(start, end - start).OrderByDescending(c => c.PreferredSource).ToArray();
            for (var i = 0; i < group.Length; i++)
                ranked[start + i] = group[i];
            start = end;
        }
        return ranked;
    }

    internal static LyricCandidate Score(LyricCandidate candidate, LyricsQuery query)
    {
        var metadata = new TrackMetadata
        {
            Title = query.Title, Artist = string.Join(", ", SplitArtists(query.Artist)),
            Album = query.Album, DurationMs = query.DurationMs
        };
        var result = new CandidateMetadata(candidate);
        var match = CompareHelper.CompareTrack(metadata, result);
        return candidate with
        {
            MatchType = match.ToString(),
            MatchScore = Math.Max(0, (int)match) / 100.0,
            TitleMatched = LyricsMatching.IsSameSongTitle(candidate.Title, query.Title),
            ArtistMatched = string.IsNullOrWhiteSpace(query.Artist) ? null : ArtistRank(candidate, query) >= 0.6,
            VersionMatched = VersionKey(candidate.Title) == VersionKey(query.Title),
            PreferredSource = candidate.SourceKey == query.PreferredSourceKey
        };
    }

    internal static SelectionDecision Decide(IReadOnlyList<LyricCandidate> ranked, LyricsQuery query)
    {
        var related = ranked.Where(c => c.TitleMatched == true && c.VersionMatched == true).ToArray();
        if (related.Length == 0)
            return new(LyricsLoadResult.NotFound, []);
        var matchedArtists = related.Where(c => c.ArtistMatched != false).ToArray();
        // 保留无匹配歌手的候选供手动纠错，但不把跨歌手降级误称为高置信自动结果。
        if (matchedArtists.Length == 0)
            return new(LyricsLoadResult.Ambiguous, []);
        var best = matchedArtists[0];
        // 歧义与回退沿用排序中的歌手档，部分匹配不能否决完整匹配的候选。
        var bestArtistRank = ArtistRank(best, query);
        matchedArtists = matchedArtists.Where(c => ArtistRank(c, query) == bestArtistRank).ToArray();
        if (best.MatchScore < 0.95 || matchedArtists.Skip(1).Any(c =>
                ConfidenceBand(c) == ConfidenceBand(best) && !SameRecording(best, c)))
            return new(LyricsLoadResult.Ambiguous, []);

        return new(LyricsLoadResult.Loaded, matchedArtists.Where(c =>
            c.MatchScore >= 0.95 && SameRecording(best, c)).ToArray());
    }

    internal static int ConfidenceBand(LyricCandidate c) => c.MatchScore >= 0.99 ? 2 : c.MatchScore >= 0.95 ? 1 : 0;

    internal static bool SameRecording(LyricCandidate a, LyricCandidate b)
        => a.SourceKey == b.SourceKey && a.Id == b.Id ||
           VersionKey(a.Title) == VersionKey(b.Title) &&
           LyricsMatching.ArtistRankKey(SplitArtists(a.Artist), string.Join(", ", SplitArtists(b.Artist))) == 1 &&
           (a.DurationMs is { } x && b.DurationMs is { } y
               ? Math.Abs((long)x - y) <= 2000
               : !string.IsNullOrWhiteSpace(a.Album) &&
                 LyricsMatching.Normalize(a.Album) == LyricsMatching.Normalize(b.Album ?? ""));

    private static double ArtistRank(LyricCandidate c, LyricsQuery query)
        => LyricsMatching.ArtistRankKey(SplitArtists(c.Artist), string.Join(", ", SplitArtists(query.Artist)));

    internal static string[] SplitArtists(string artist)
        => artist.Split(ArtistSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(NormalizeArtistName).ToArray();

    private static string NormalizeArtistName(string artist)
    {
        // 平台常在歌手名后追加括号别名；只归一比较值，保留查询和候选中的原始名称。
        var open = artist.LastIndexOfAny(['(', '（']);
        return open > 0 && artist[^1] is (')' or '）')
            ? artist[..open].TrimEnd()
            : artist;
    }

    internal static string VersionKey(string title)
        => string.Join("|", VersionTags.Matches(title).Select(m => m.Value.ToLowerInvariant() switch
        {
            var v when v.StartsWith("live") || v.StartsWith("现场") => "live",
            "remix" or "混音" or "混音版" => "remix",
            "伴奏" or "伴奏版" or "纯音乐" or "instrumental" or "off vocal" => "instrumental",
            var v => v
        }).Distinct().Order());

    private sealed class CandidateMetadata(LyricCandidate candidate) : ISearchResult
    {
        public ISearcher Searcher => null!;
        public string Title => candidate.Title;
        public string[] Artists => SplitArtists(candidate.Artist);
        public string Artist => string.Join(", ", Artists);
        public string Album => candidate.Album!;
        public string[] AlbumArtists => [];
        public string AlbumArtist => string.Empty;
        public int? DurationMs => candidate.DurationMs;
        public CompareHelper.MatchType? MatchType { get; set; }
    }
}

internal sealed record SelectionDecision(LyricsLoadResult Status, IReadOnlyList<LyricCandidate> Candidates);
