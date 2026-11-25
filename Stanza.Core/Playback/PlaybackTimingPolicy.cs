namespace Stanza.Core.Playback;

/// <summary>时间线可信度、位置外推、暂停冻结和 seek 阈值策略。</summary>
internal sealed record PlaybackTimingPolicy(
    bool TrustLastUpdatedTime,
    bool AllowFreshSnapshotExtrapolation,
    int FreshSnapshotMaxAgeMs,
    bool AllowWallClockFallback,
    bool TrustPositionWhenPaused,
    int PauseSeekThresholdMs)
{
    public static PlaybackTimingPolicy Default { get; } = new(
        TrustLastUpdatedTime: true,
        AllowFreshSnapshotExtrapolation: true,
        FreshSnapshotMaxAgeMs: 2000,
        AllowWallClockFallback: true,
        TrustPositionWhenPaused: true,
        PauseSeekThresholdMs: 2000);
}
