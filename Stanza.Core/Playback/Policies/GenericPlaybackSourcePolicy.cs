using Stanza.Core.Models;

namespace Stanza.Core.Playback.Policies;

/// <summary>未知播放器的保守策略，沿用当前公共默认行为。</summary>
/// <summary>
///     未知播放器的保守策略：沿用标准 SMTC 时间线行为，不假设任何专有事件序列。
/// </summary>
internal sealed class GenericPlaybackSourcePolicy : IPlaybackSourcePolicy
{
    public string Key => PlaybackSourceIds.AutoFallback;
    public string DisplayName => "通用播放器";

    public PlaybackSwitchPolicy Switch { get; } = PlaybackSwitchPolicy.Default;
    public PlaybackTimingPolicy Timing { get; } = PlaybackTimingPolicy.Default;

    public PlaybackCapabilities Capabilities { get; } = new(
        SupportsPrevious: true,
        SupportsPlayPause: true,
        SupportsNext: true,
        SupportsSeek: false,
        ProvidesCover: true);

    public bool Matches(string? sourceAppUserModelId) => true;

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
