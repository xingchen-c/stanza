namespace Stanza.Core.Models;

/// <summary>歌词搜索/匹配的统一输入；后续维度只扩展此对象，不扩散到所有歌词源签名。</summary>
public sealed record LyricsQuery(
    string Title,
    string Artist,
    int? DurationMs,
    string? Album = null,
    string? PreferredSourceKey = null)
{
    /// <summary>当前应用内稳定的歌曲 key。</summary>
    public string Key => TrackKey.Of(Title, Artist);
}
