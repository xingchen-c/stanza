namespace Stanza.Core.Playback;

/// <summary>当前播放会话支持的交互能力，用于驱动按钮和媒体区显隐。</summary>
/// <param name="SupportsPrevious">支持上一首。</param>
/// <param name="SupportsPlayPause">支持播放或暂停。</param>
/// <param name="SupportsNext">支持下一首。</param>
/// <param name="SupportsSeek">支持跳转播放位置；当前尚未接入 UI。</param>
/// <param name="ProvidesCover">当前播放源能够提供封面。</param>
public sealed record PlaybackCapabilities(
    bool SupportsPrevious,
    bool SupportsPlayPause,
    bool SupportsNext,
    bool SupportsSeek,
    bool ProvidesCover);
