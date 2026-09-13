using Stanza.Models;

namespace Stanza.Services.Playback;

/// <summary>从 SMTC 会话描述中选择当前应跟随的目标会话。</summary>
internal static class PlaybackSourceSelector
{
    public static int SelectIndex(
        IReadOnlyList<PlaybackSessionDescriptor> sessions,
        string requestedSource,
        string? currentSessionId)
    {
        if (sessions.Count == 0)
            return -1;

        var currentIndex = FindIndexById(sessions, currentSessionId);
        if (string.Equals(
                requestedSource,
                PlaybackSourceIds.Auto,
                StringComparison.OrdinalIgnoreCase) &&
            currentIndex >= 0)
            return currentIndex;

        var candidates = Enumerable.Range(0, sessions.Count);

        if (!string.Equals(
                requestedSource,
                PlaybackSourceIds.Auto,
                StringComparison.OrdinalIgnoreCase))
        {
            var requestedPolicy = PlaybackSourceRegistry.All.FirstOrDefault(
                p => string.Equals(
                    p.Key,
                    requestedSource,
                    StringComparison.OrdinalIgnoreCase));

            if (requestedPolicy is null)
                return -1;

            candidates = candidates.Where(index =>
                requestedPolicy.Matches(sessions[index].SourceAppUserModelId));
        }

        var ordered = candidates
            .OrderBy(index => StateRank(sessions[index].PlaybackState))
            .ThenByDescending(index => sessions[index].IsCurrentSession)
            .ThenByDescending(index => index == currentIndex)
            .ThenBy(index => sessions[index].SessionId, StringComparer.Ordinal)
            .ToList();

        return ordered.Count > 0 ? ordered[0] : -1;
    }

    private static int FindIndexById(
        IReadOnlyList<PlaybackSessionDescriptor> sessions,
        string? sessionId)
    {
        if (string.IsNullOrEmpty(sessionId))
            return -1;

        for (var i = 0; i < sessions.Count; i++)
        {
            if (string.Equals(
                    sessions[i].SessionId,
                    sessionId,
                    StringComparison.Ordinal))
                return i;
        }

        return -1;
    }

    private static int StateRank(PlaybackState state)
    {
        return state switch
        {
            PlaybackState.Playing or PlaybackState.Changing => 0,
            PlaybackState.Paused => 1,
            PlaybackState.Stopped => 2,
            PlaybackState.Unknown => 3,
            PlaybackState.Closed => 4,
            _ => 5
        };
    }
}
