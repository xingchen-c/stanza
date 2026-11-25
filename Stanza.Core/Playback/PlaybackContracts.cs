using Stanza.Core.Models;

namespace Stanza.Core.Playback;

/// <summary>会话选择器使用的轻量会话描述，不包含 WinRT Session 对象。</summary>
// SessionId 只在当前进程内使用，可由协调器按枚举顺序生成，例如：{SourceAppUserModelId}:{index}
internal readonly record struct PlaybackSessionDescriptor(
    string SessionId,
    string SourceAppUserModelId,
    PlaybackState PlaybackState,
    bool IsCurrentSession);

/// <summary>一次播放位置采样的结果，包含原始值、合成值和渲染决策。</summary>
internal readonly record struct PlaybackSample(
    int PositionMs,
    int RawPositionMs,
    PlaybackState PlaybackState,
    bool ShouldRender,
    string? SkipReason);

/// <summary>播放源策略规整后的曲目元数据。</summary>
internal readonly record struct PlaybackMetadata(
    string Title,
    string Artist,
    string Album,
    int? DurationMs);

/// <summary>封面读取请求：由公共协调器提供统一上下文，适配器只负责取图。</summary>
internal readonly record struct PlaybackCoverRequest(
    string TrackKey,
    PlaybackMedia Props,
    long NowUtcMs);

/// <summary>适配器对一次封面读取请求的处理结果。</summary>
internal enum PlaybackCoverAction
{
    NoChange,
    NoCover,
    Available
}

/// <summary>适配器返回原始图片字节；工作流去重，展示层解码。</summary>
internal readonly record struct PlaybackCoverResult(
    PlaybackCoverAction Action,
    byte[]? Data = null)
{
    public static PlaybackCoverResult NoChange => new(PlaybackCoverAction.NoChange);
    public static PlaybackCoverResult NoCover => new(PlaybackCoverAction.NoCover);
    public static PlaybackCoverResult Available(byte[] data) => new(PlaybackCoverAction.Available, data);
}
