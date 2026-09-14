namespace Stanza.Models;

/// <summary>播放源模式与策略 Key。字符串值用于设置持久化和策略注册。</summary>
internal static class PlaybackSourceIds
{
    /// <summary>自动选择系统当前会话，并按会话来源匹配策略。</summary>
    public const string Auto = "Auto";

    /// <summary>QQ 音乐播放源。</summary>
    public const string QqMusic = "QQ";

    /// <summary>网易云音乐播放源。</summary>
    public const string NetEase = "NetEase";

    /// <summary>未知播放器使用的通用策略。</summary>
    public const string AutoFallback = "Generic";
}
