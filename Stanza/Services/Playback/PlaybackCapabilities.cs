namespace Stanza.Services.Playback;

// 播放能力
internal sealed record PlaybackCapabilities(
    // 支持上一个
    bool SupportsPrevious,

    // 支持播放/暂停
    bool SupportsPlayPause,

    // 支持下一曲
    bool SupportsNext,

    // 支持搜索
    bool SupportsSeek,

    // 提供封面
    bool ProvidesCover);