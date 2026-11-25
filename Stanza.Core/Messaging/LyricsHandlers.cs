using Stanza.Core.Workflows;

namespace Stanza.Core.Messaging;

internal sealed class ApplyLyricsCandidateHandler(PlaybackContext playback, LyricsWorkflow lyrics)
    : ICommandHandler<ApplyLyricsCandidate, bool>
{
    public async Task<bool> HandleAsync(ApplyLyricsCandidate command, CancellationToken cancellationToken)
    {
        if (!playback.Requests.IsSelectionCurrent(command.Context)) return false;
        var applied = await lyrics.ApplyCandidateAsync(command.Context.Key, command.Candidate, cancellationToken);
        // 歌词流程校验请求世代，这里还需确认原选择窗口的曲目 revision，不能接受跨曲结果。
        if (!applied || !playback.Requests.IsSelectionCurrent(command.Context)) return false;
        playback.LyricsRequested = true;
        return true;
    }
}
internal sealed class SetLyricOffsetHandler(PlaybackContext playback, ILyricsOffsetStore offsets)
    : ICommandHandler<SetLyricOffset, bool>
{
    public Task<bool> HandleAsync(SetLyricOffset command, CancellationToken cancellationToken)
    {
        if (!playback.Requests.IsSelectionCurrent(command.Context) ||
            command.OffsetMs is < -ILyricsOffsetStore.MaximumOffsetMs or > ILyricsOffsetStore.MaximumOffsetMs)
            return Task.FromResult(false);
        return Task.FromResult(offsets.Set(command.Context.Key, command.OffsetMs));
    }
}
/// <summary>重新执行本曲自动匹配；取消等待不撤销用例已经接受的记忆与缓存清理。</summary>
internal sealed class RestoreAutomaticLyricsHandler(PlaybackContext playback, LyricsWorkflow lyrics)
    : ICommandHandler<RestoreAutomaticLyrics, bool>
{
    public async Task<bool> HandleAsync(RestoreAutomaticLyrics command, CancellationToken cancellationToken)
    {
        if (!playback.Requests.IsSelectionCurrent(command.Context)) return false;
        var restored = await lyrics.RestoreAutomaticAsync(command.Context.Query, cancellationToken);
        if (!restored || !playback.Requests.IsSelectionCurrent(command.Context)) return false;
        playback.LyricsRequested = true;
        return true;
    }
}
internal sealed class ClearLyricsDataHandler(LyricsWorkflow lyrics) : ICommandHandler<ClearLyricsData, bool>
{
    public Task<bool> HandleAsync(ClearLyricsData command, CancellationToken cancellationToken)
    { lyrics.ClearAll(); return Task.FromResult(true); }
}
