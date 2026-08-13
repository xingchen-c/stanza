using Lyricify.Lyrics.Models;

namespace FluentLyrics.Services.Lyrics;

/// <summary>
///     歌词解析共享工具：QQ / LRCLIB 等词源共用的行时间戳读取，
///     避免每个 provider 各写一份相同的 TryGetTiming。
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
}
