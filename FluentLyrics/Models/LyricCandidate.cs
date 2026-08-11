namespace FluentLyrics.Models;

/// <summary>一个歌词候选（手动选择歌词用）：来自某歌词源的一次搜索结果</summary>
public sealed record LyricCandidate(
    string SourceKey,   // 来源 Key（如 "QQ"），对应 ILyricsProvider.Key
    string SourceName,  // 来源显示名（如 "QQ 音乐"）
    string Id,          // 源内歌曲 ID（取词用）
    string Title,       // 候选歌名
    string Artist,      // 候选歌手
    string? Album,      // 候选专辑（可能为空）
    int? DurationMs,    // 候选时长（可能为空）
    string? MatchType); // 匹配度（如 "Perfect" / "PrettyHigh"，可能为空）
