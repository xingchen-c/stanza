namespace FluentLyrics.Models;

/// <summary>一首歌的歌词行：毫秒开始时间 + 文本</summary>
public sealed class LyricLine(int startTime, string text, int? endTime = null)
{
    public int StartTime { get; } = startTime;
    public string Text { get; } = text;

    /// <summary>行结束时间（毫秒）；QQ 歌词一般都有，个别行可能缺失</summary>
    public int? EndTime { get; } = endTime;
}
