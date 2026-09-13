using Stanza.Models;
using Stanza.Services.Playback.Policies;

namespace Stanza.Services.Playback;

internal static class PlaybackSourceRegistry
{
    private static readonly IPlaybackSourcePolicy Fallback = new GenericPlaybackSourcePolicy();

    private static readonly IPlaybackSourcePolicy[] SpecificPolicies =
    [
        new QqMusicPlaybackSourcePolicy()
    ];

    public static IReadOnlyList<IPlaybackSourcePolicy> All { get; } =
        [.. SpecificPolicies, Fallback];

    public static IPlaybackSourcePolicy ResolveByKey(string? key)
    {
        return All.FirstOrDefault(p => string.Equals(
                   p.Key,
                   key,
                   StringComparison.OrdinalIgnoreCase))
               ?? ResolveByKey(PlaybackSourceIds.AutoFallback);
    }

    public static IPlaybackSourcePolicy ResolveBySession(string? aumid)
    {
        return SpecificPolicies.FirstOrDefault(p => p.Matches(aumid)) ?? Fallback;
    }
}
