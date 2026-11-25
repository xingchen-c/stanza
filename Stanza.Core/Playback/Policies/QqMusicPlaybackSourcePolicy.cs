using Stanza.Core.Models;

namespace Stanza.Core.Playback.Policies;

/// <summary>
///     QQ 音乐桌面版策略：时间线完整可用，支持 Position 外推、时长变化早期切歌，
///     并使用 Stopped / Changing 信号区分切歌序列。
/// </summary>
internal sealed class QqMusicPlaybackSourcePolicy : IPlaybackSourcePolicy
{
    private static readonly string[] QqAumids =
    [
        // 实机确认：Windows SMTC 返回的 QQ 音乐 SourceAppUserModelId。
        "QQMusic.exe"
    ];

    public string Key => PlaybackSourceIds.QqMusic;
    public string DisplayName => "QQ 音乐";

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
        UseStoppedAsEarlySignal: true,
        UseChangingAsEarlySignal: true,
        UseDurationChangeAsEarlySignal: true,
        UsePositionRegressionAsEarlySignal: true,
        StaleSnapshotWindowMs: 3000,
        PositionGlitchWindowMs: 2000,
        PositionGlitchThresholdMs: 10000);

    public PlaybackTimingPolicy Timing { get; } = new(
        TrustLastUpdatedTime: true,
        AllowFreshSnapshotExtrapolation: true,
        FreshSnapshotMaxAgeMs: 2000,
        AllowWallClockFallback: true,
        TrustPositionWhenPaused: true,
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
               QqAumids.Any(x => string.Equals(
                   x, aumid, StringComparison.OrdinalIgnoreCase));
    }

    public PlaybackMetadata NormalizeMetadata(PlaybackMetadata raw)
    {
        // 第一版保持行为不变，只做基础 trim。
        // 标题清洗仍留在 Lyrics provider 的搜索匹配层，不在这里重复实现。
        return raw with
        {
            Title = raw.Title.Trim(),
            Artist = raw.Artist.Trim(),
            Album = raw.Album.Trim()
        };
    }
}
