namespace Stanza.Core.Lyrics;

internal static class LyricsTimeline
{
    internal static int GetLineIndexAt(IReadOnlyList<LyricLine> lines, int positionMs)
    {
        if (lines.Count == 0 || positionMs < 0)
            return -1;
        if (positionMs < lines[0].StartTime)
            return 0;

        int lo = 0, hi = lines.Count - 1, result = -1;
        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            if (lines[mid].StartTime <= positionMs)
            {
                result = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return result;
    }
}
