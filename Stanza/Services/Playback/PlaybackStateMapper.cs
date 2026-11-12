using Windows.Media.Control;
using Stanza.Models;

namespace Stanza.Services.Playback;

/// <summary>把 WinRT SMTC 播放状态映射为项目内部状态。</summary>
internal static class PlaybackStateMapper
{
    public static PlaybackState Map(
        GlobalSystemMediaTransportControlsSessionPlaybackStatus status)
    {
        return status switch
        {
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing => PlaybackState.Playing,
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused => PlaybackState.Paused,
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Stopped => PlaybackState.Stopped,
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Changing => PlaybackState.Changing,
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Closed => PlaybackState.Closed,
            _ => PlaybackState.Unknown
        };
    }
}
