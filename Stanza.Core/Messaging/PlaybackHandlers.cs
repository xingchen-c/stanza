using Stanza.Core.Workflows;

namespace Stanza.Core.Messaging;

internal sealed class PreviousTrackHandler(PlaybackWorkflow playback) : ICommandHandler<PreviousTrack, bool>
{
    public async Task<bool> HandleAsync(PreviousTrack command, CancellationToken cancellationToken)
    { await playback.TrySkipPreviousAsync(); return true; }
}
internal sealed class TogglePlaybackHandler(PlaybackWorkflow playback) : ICommandHandler<TogglePlayback, bool>
{
    public async Task<bool> HandleAsync(TogglePlayback command, CancellationToken cancellationToken)
    { await playback.TryTogglePlayPauseAsync(); return true; }
}
internal sealed class NextTrackHandler(PlaybackWorkflow playback) : ICommandHandler<NextTrack, bool>
{
    public async Task<bool> HandleAsync(NextTrack command, CancellationToken cancellationToken)
    { await playback.TrySkipNextAsync(); return true; }
}
internal sealed class SelectPlaybackSourceHandler(IPlaybackGateway gateway) : ICommandHandler<SelectPlaybackSource, bool>
{
    public Task<bool> HandleAsync(SelectPlaybackSource command, CancellationToken cancellationToken)
    { gateway.SetPlaybackSource(command.Mode); return Task.FromResult(true); }
}
internal sealed class SetMediaControlsHandler(PlaybackWorkflow playback) : ICommandHandler<SetMediaControls, bool>
{
    public Task<bool> HandleAsync(SetMediaControls command, CancellationToken cancellationToken)
    { playback.SetMediaControlsEnabled(command.Enabled); return Task.FromResult(true); }
}
internal sealed class RejectCoverHandler(CoverWorkflow cover) : ICommandHandler<RejectCover, bool>
{
    public Task<bool> HandleAsync(RejectCover command, CancellationToken cancellationToken)
    { cover.RejectCurrent(); return Task.FromResult(true); }
}
