namespace FluentLyrics.Models;

/// <summary>一个逐字歌词时间点：文本片段 + 绝对起止时间（毫秒）</summary>
public sealed record LyricWord(string Text, int StartTime, int EndTime);

/// <summary>一首歌的歌词行：毫秒开始时间 + 文本</summary>
public sealed class LyricLine(
    int startTime,
    string text,
    int? endTime = null,
    string? translation = null,
    List<LyricWord>? words = null)
{
    public int StartTime { get; } = startTime;
    public string Text { get; } = text;

    /// <summary>行结束时间（毫秒）；QQ 歌词一般都有，个别行可能缺失</summary>
    public int? EndTime { get; } = endTime;

    /// <summary>该行翻译（QQ 歌词接口 Trans 字段，与正词同时间轴对齐）；可能为空</summary>
    public string? Translation { get; } = translation;

    /// <summary>逐字时间戳（QQ 逐字歌词）；可能为空 → 渲染回退为整行线性揭晓</summary>
    public List<LyricWord>? Words { get; } = words;

    /// <summary>是否有可用的逐字时间戳</summary>
    public bool HasWordTiming => Words is { Count: > 0 };
}
