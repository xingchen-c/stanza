namespace FluentLyrics.Models;

/// <summary>歌词源返回的结果：状态 + 行列表</summary>
public sealed record LyricsResult(LyricsLoadResult Status, IReadOnlyList<LyricLine> Lines);
