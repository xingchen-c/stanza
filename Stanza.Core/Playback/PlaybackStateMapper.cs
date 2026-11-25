
using Stanza.Core.Models;

namespace Stanza.Core.Playback;

/// <summary>把 WinRT SMTC 播放状态映射为项目内部状态。</summary>
internal static class PlaybackStateMapper
{
    public static PlaybackState Map(
        RawPlaybackStatus status)
    {
        return status switch
        {
            RawPlaybackStatus.Playing => PlaybackState.Playing,
            RawPlaybackStatus.Paused => PlaybackState.Paused,
            RawPlaybackStatus.Stopped => PlaybackState.Stopped,
            RawPlaybackStatus.Changing => PlaybackState.Changing,
            RawPlaybackStatus.Closed => PlaybackState.Closed,
            _ => PlaybackState.Unknown
        };
    }
}
