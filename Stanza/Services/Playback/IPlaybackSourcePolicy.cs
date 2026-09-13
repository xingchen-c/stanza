namespace Stanza.Services.Playback;

internal interface IPlaybackSourcePolicy
{
    string Key { get; }
    string DisplayName { get; }

    bool Matches(string? sourceAppUserModelId);
    // 规范化的元数据
    PlaybackMetadata NormalizeMetadata(PlaybackMetadata raw);

    PlaybackSwitchPolicy Switch { get; }
    PlaybackTimingPolicy Timing { get; }
    PlaybackCapabilities Capabilities { get; }
}