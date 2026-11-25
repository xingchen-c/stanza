namespace Stanza.Core.Models;

/// <summary>项目自己的播放状态，隔离 UI 与 WinRT SMTC 枚举。</summary>
public enum PlaybackState
{
    /// <summary>无法读取播放状态。</summary>
    Unknown,
    /// <summary>正在播放。</summary>
    Playing,
    /// <summary>已暂停。</summary>
    Paused,
    /// <summary>已停止。</summary>
    Stopped,
    /// <summary>播放器正在切换媒体或缓冲区。</summary>
    Changing,
    /// <summary>会话已关闭。</summary>
    Closed

}
