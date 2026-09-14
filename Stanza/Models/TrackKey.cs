namespace Stanza.Models;

/// <summary>
///     歌曲唯一键（歌名 + 歌手）：歌词缓存 / 手动选择记忆 / 切歌 key 比较共用。
///     单点定义，避免各调用处对 null/空白歌手处理不一致导致缓存 key 错位。
/// </summary>
public static class TrackKey
{
    /// <summary>用不可见分隔符组合歌名和歌手，避免字符串拼接歧义。</summary>
    public static string Of(string title, string? artist)
    {
        return $"{title}\u0001{artist ?? string.Empty}";
    }
}
