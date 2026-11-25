using Stanza.Core.Models;

namespace Stanza.Core.Playback.Policies;

/// <summary>
///     网易云音乐桌面版策略。
///     实机行为：SMTC 时间线退化，Position 恒为 0、LastUpdatedTime 恒为 1601 年，
///     且不再发送 TimelinePropertiesChanged。位置只能从切歌/恢复边界做墙钟合成。
/// </summary>
internal sealed class NeteasePlaybackSourcePolicy : IPlaybackSourcePolicy
{
    private static readonly string[] NeteaseAumids =
    [
        // 实机确认：Windows SMTC 返回的网易云音乐 SourceAppUserModelId。
        "cloudmusic.exe"
    ];

    public string Key => PlaybackSourceIds.NetEase;
    public string DisplayName => "网易云音乐";

    public PlaybackSwitchPolicy Switch { get; } = new(
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
        UseStoppedAsEarlySignal: false,
        UseChangingAsEarlySignal: false,
        UseDurationChangeAsEarlySignal: false,
        UsePositionRegressionAsEarlySignal: false,
        StaleSnapshotWindowMs: 0,
        PositionGlitchWindowMs: 0,
        PositionGlitchThresholdMs: 0);

    public PlaybackTimingPolicy Timing { get; } = new(
        TrustLastUpdatedTime: false,
        AllowFreshSnapshotExtrapolation: false,
        FreshSnapshotMaxAgeMs: 2000,
        AllowWallClockFallback: true,
        TrustPositionWhenPaused: false,
        PauseSeekThresholdMs: 2000);

    public PlaybackCapabilities Capabilities { get; } = new(
        SupportsPrevious: true,
        SupportsPlayPause: true,
        SupportsNext: true,
        SupportsSeek: false,
        ProvidesCover: true);

    public bool Matches(string? aumid)
    {
        return aumid is not null &&
               NeteaseAumids.Any(x => string.Equals(
                   x,
                   aumid,
                   StringComparison.OrdinalIgnoreCase));
    }

    public PlaybackMetadata NormalizeMetadata(PlaybackMetadata raw)
    {
        return raw with
        {
            Title = raw.Title.Trim(),
            Artist = raw.Artist.Trim(),
            Album = raw.Album.Trim()
        };
    }
}
