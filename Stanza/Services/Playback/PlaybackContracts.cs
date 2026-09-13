using Stanza.Models;

namespace Stanza.Services.Playback;

// 会话选择器
// SessionId 只在当前进程内使用，可由协调器按枚举顺序生成，例如：{SourceAppUserModelId}:{index}
internal readonly record struct PlaybackSessionDescriptor(
    string SessionId,
    string SourceAppUserModelId,
    PlaybackState PlaybackState,
    bool IsCurrentSession);

// 采样接口返回
internal readonly record struct PlaybackSample(
    int PositionMs,
    int RawPositionMs,
    PlaybackState PlaybackState,
    bool ShouldRender,
    string? SkipReason);

// 播放的元数据
internal readonly record struct PlaybackMetadata(
    string Title,
    string Artist,
    string Album,
    int? DurationMs);
