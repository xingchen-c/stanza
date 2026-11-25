namespace Stanza.Core.Playback;

/// <summary>播放业务状态的唯一所有者，不持有平台会话。</summary>
/// <remarks>在 IStateDispatcher 上读写；UI 通过只读投影访问，不直接持有此上下文。</remarks>
internal sealed class PlaybackContext
{
    internal string CurrentKey = string.Empty;
    // 会话与曲目身份归播放模块；同曲歌词请求的接管另由 LyricsContext.Generation 管理。
    internal readonly MediaRequestIdentity Requests = new();
    internal bool LyricsRequested;
    internal readonly TrackSwitchStateMachine Switch = new();
    internal RawPlaybackStatus? LastPlaybackStatus;
    // 事件去重独立于采样：先被采样发现的变化仍可能需要处理后到的播放事件。
    internal RawPlaybackStatus? LastEventPlaybackStatus;
    internal NowPlaying? NowPlaying;
    internal long LastSwitchStartUtcMs;
    internal double LastEndTimeMs = -1;
    internal readonly TrackDurationEvidence DurationEvidence = new();
    internal int LastTickPositionMs = -1;
    internal int LastTimelineEventLogMs;
    internal PlaybackState PlaybackState = PlaybackState.Unknown;
    internal PlaybackCapabilities Capabilities = new(false, false, false, false, false);
    internal IPlaybackSourcePolicy SourcePolicy = PlaybackSourceRegistry.ResolveByKey(PlaybackSourceIds.AutoFallback);
    internal PlaybackPositionTracker PositionTracker = new(PlaybackTimingPolicy.Default, PlaybackSwitchPolicy.Default);
    internal string SourceAppUserModelId = "?";
    internal string? LastNoSessionStatus;
    internal bool Connected;
    internal string Status = string.Empty;
    // 保存最近一次业务采样；换会话/换曲时先禁用渲染，不能把旧位置用于新歌词。
    internal PlaybackSample Sample = new(0, 0, PlaybackState.Unknown, false, "无会话");
}
