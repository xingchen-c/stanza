using Lyricify.Lyrics.Models;
using Stanza.Models;

namespace Stanza.Services.Lyrics;

/// <summary>
///     歌词解析共享工具：QQ / 网易云 / LRCLIB 等词源共用的行时间戳读取与逐字提取，
///     避免每个 provider 各写一份相同的逻辑。
/// </summary>
internal static class LyricParsing
{
    /// <summary>安全读取一行的开始/结束时间与文本（跳过空行、纯伴奏行等异常行）</summary>
    public static bool TryGetTiming(ILineInfo line, out int startTime, out string text, out int? endTime)
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

    /// <summary>从 SyllableLineInfo 提取逐字时间戳（QRC/YRC 逐字行）；普通行（无逐字数据）返回 null</summary>
    public static List<LyricWord>? BuildWords(ILineInfo line)
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
}
