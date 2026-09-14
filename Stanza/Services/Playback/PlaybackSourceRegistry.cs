using Stanza.Models;
using Stanza.Services.Playback.Policies;

namespace Stanza.Services.Playback;

/// <summary>
///     编译期播放源注册表：把来源策略和平台适配器绑定在一起，按 Key 或 AUMID 解析。
/// </summary>
internal static class PlaybackSourceRegistry
{
    private sealed record Registration(
        IPlaybackSourcePolicy Policy,
        IPlaybackSourceAdapter Adapter);

    private static readonly Registration Fallback = new(
        new GenericPlaybackSourcePolicy(),
        new SmtcPlaybackSourceAdapter(PlaybackSourceIds.AutoFallback, providesCover: true));

    private static readonly Registration[] SpecificSources =
    [
        new(
            new QqMusicPlaybackSourcePolicy(),
            new SmtcPlaybackSourceAdapter(PlaybackSourceIds.QqMusic, providesCover: true)),
        new(
            new NeteasePlaybackSourcePolicy(),
            new SmtcPlaybackSourceAdapter(
                PlaybackSourceIds.NetEase,
                providesCover: true,
                allowSameTrackReplacement: true,
                replacementWindowMs: 5000,
                readThrottleMs: 250))
    ];

    /// <summary>所有策略，包含最后注册的 Generic fallback。</summary>
    public static IReadOnlyList<IPlaybackSourcePolicy> All { get; } =
        [.. SpecificSources.Select(x => x.Policy), Fallback.Policy];

    /// <summary>按策略 Key 解析，未知 Key 回退 Generic。</summary>
    public static IPlaybackSourcePolicy ResolveByKey(string? key)
    {
        return All.FirstOrDefault(p => string.Equals(
                   p.Key,
                   key,
                   StringComparison.OrdinalIgnoreCase))
               ?? ResolveByKey(PlaybackSourceIds.AutoFallback);
    }

    /// <summary>按 AUMID 匹配具体来源，未知来源回退 Generic。</summary>
    public static IPlaybackSourcePolicy ResolveBySession(string? aumid)
    {
        return SpecificSources.FirstOrDefault(x => x.Policy.Matches(aumid))?.Policy ?? Fallback.Policy;
    }

    /// <summary>按策略 Key 解析对应平台适配器。</summary>
    public static IPlaybackSourceAdapter ResolveAdapterByKey(string? key)
    {
        var registration = SpecificSources.FirstOrDefault(x => string.Equals(
                               x.Policy.Key,
                               key,
                               StringComparison.OrdinalIgnoreCase))
                           ?? Fallback;
        return registration.Adapter;
    }

    /// <summary>按 AUMID 解析对应平台适配器。</summary>
    public static IPlaybackSourceAdapter ResolveAdapterBySession(string? aumid)
    {
        return SpecificSources.FirstOrDefault(x => x.Policy.Matches(aumid))?.Adapter
               ?? Fallback.Adapter;
    }
}
