namespace Stanza.Models;

/// <summary>
///     一个歌词候选（手动选择歌词用）：来自某歌词源的一次搜索结果。
///     MatchScore / ArtistMatched 为增量字段，旧版 lyrics-choice.json 缺失时保持默认值。
/// </summary>
public sealed record LyricCandidate(
    string SourceKey,   // 来源 Key（如 "QQ"），对应 ILyricsProvider.Key
    string SourceName,  // 来源显示名（如 "QQ 音乐"）
    string Id,          // 源内歌曲 ID（取词用）
    string Title,       // 候选歌名
    string Artist,      // 候选歌手
    string? Album,      // 候选专辑（可能为空）
    int? DurationMs,    // 候选时长（可能为空）
    string? MatchType)  // 匹配度（如 "Perfect" / "PrettyHigh"，可能为空）
{
    /// <summary>候选相对当前歌曲的综合匹配分；旧版选择记忆可能没有该字段。</summary>
    public double MatchScore { get; init; }

    /// <summary>是否至少与一位期望歌手相交；null 表示当前歌曲没有可用歌手信息。</summary>
    public bool? ArtistMatched { get; init; }

    /// <summary>true 表示与当前歌曲核心歌名一致，false 为其它搜索结果；旧版记录可能未标注。</summary>
    public bool? TitleMatched { get; init; }
}
