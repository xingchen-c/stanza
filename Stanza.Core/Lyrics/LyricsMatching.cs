namespace Stanza.Core.Lyrics;

internal static class LyricsMatching
{
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

    internal static string Normalize(string s)
    {
        return string.Concat(s.Where(ch => !char.IsWhiteSpace(ch))).ToLowerInvariant();
    }
    private static bool IsVersionSuffix(string suffix)
        => Normalize(suffix) is "live" or "liveversion" or "live版" or "remix" or "remix版"
            or "伴奏" or "伴奏版" or "现场版" or "剧情版";
    private static readonly char[] ArtistSeparators = ['/', ',', '，', '、', '&', ';', '；'];
    internal static double ArtistRankKey(string[]? artists, string? expected) => ArtistScore(artists, expected) ?? 0;
}
