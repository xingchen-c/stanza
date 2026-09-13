namespace Stanza.Services.Playback;

// 切歌策略
internal sealed record PlaybackSwitchPolicy(
    int PositionRegressionThresholdMs,
    int ConfirmationPollIntervalMs,
    int ConfirmationRequiredCount,
    int ConfirmationMaxAttempts,
    int MetadataPollIntervalMs,
    int MetadataPollMaxAttempts,
    int SwitchWatchdogMs,
    int FalseSwitchRecoveryMs,
    int RecentSwitchIgnoreMs,
    int DurationChangeThresholdMs,
    bool UseStoppedAsEarlySignal,
    bool UseChangingAsEarlySignal,
    bool UseDurationChangeAsEarlySignal,
    bool UsePositionRegressionAsEarlySignal,
    int StaleSnapshotWindowMs,
    int PositionGlitchWindowMs,
    int PositionGlitchThresholdMs)
{
    public static PlaybackSwitchPolicy Default { get; } = new(
        PositionRegressionThresholdMs: 2000,
        ConfirmationPollIntervalMs: 200,
        ConfirmationRequiredCount: 2,
        ConfirmationMaxAttempts: 10,
        MetadataPollIntervalMs: 200,
        MetadataPollMaxAttempts: 20,
        SwitchWatchdogMs: 8000,
        FalseSwitchRecoveryMs: 3000,
        RecentSwitchIgnoreMs: 2000,
        DurationChangeThresholdMs: 500,
        UseStoppedAsEarlySignal: true,
        UseChangingAsEarlySignal: true,
        UseDurationChangeAsEarlySignal: true,
        UsePositionRegressionAsEarlySignal: true,
        StaleSnapshotWindowMs: 3000,
        PositionGlitchWindowMs: 2000,
        PositionGlitchThresholdMs: 10000);
}