using Stanza.Models;

namespace Stanza.Services.Playback.Policies;

/// <summary>未知播放器的保守策略，沿用当前公共默认行为。</summary>
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
