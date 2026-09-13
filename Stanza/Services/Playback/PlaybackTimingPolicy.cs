namespace Stanza.Services.Playback;

// 时间线策略
internal sealed record PlaybackTimingPolicy(
    bool TrustLastUpdatedTime,
    bool AllowFreshSnapshotExtrapolation,
    int FreshSnapshotMaxAgeMs,
    bool AllowWallClockFallback,
    int PauseSeekThresholdMs)
{
    public static PlaybackTimingPolicy Default { get; } = new(
        TrustLastUpdatedTime: true,
        AllowFreshSnapshotExtrapolation: true,
        FreshSnapshotMaxAgeMs: 2000,
        AllowWallClockFallback: true,
        PauseSeekThresholdMs: 2000);
}