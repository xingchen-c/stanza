namespace Stanza.Core.Models;

/// <summary>
///     当前曲目的内部快照。由 SMTC 元数据、播放源策略和播放状态规整后产生，
///     供协调器、歌词来源和 UI 读取，避免继续向上层暴露原始 WinRT 对象。
/// </summary>
internal sealed record NowPlaying(
    string Key,
    string Title,
    string Artist,
    string Album,
    int? DurationMs,
    PlaybackState PlaybackState,
    string SourceKey,
    string SourceAppUserModelId);
