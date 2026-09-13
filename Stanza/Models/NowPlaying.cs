namespace Stanza.Models;

internal sealed record NowPlaying(
    string Key,
    string Title,
    string Artist,
    string Album,
    int? DurationMs,
    PlaybackState PlaybackState,
    string SourceKey,
    string SourceAppUserModelId);