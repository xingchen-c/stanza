namespace Stanza.Core.Messaging;

/// <summary>用户操作的参数与返回类型；执行逻辑交给唯一 Handler。</summary>
internal interface ICommand<TResult>;
internal interface ICommandHandler<in TCommand, TResult> where TCommand : ICommand<TResult>
{
    Task<TResult> HandleAsync(TCommand command, CancellationToken cancellationToken);
}
/// <summary>应用操作入口，负责状态上下文调度、取消传播和任务跟踪。</summary>
internal interface ICommandBus
{
    Task<TResult> SendAsync<TResult>(ICommand<TResult> command, CancellationToken cancellationToken = default);
}

internal sealed record PreviousTrack : ICommand<bool>;
internal sealed record TogglePlayback : ICommand<bool>;
internal sealed record NextTrack : ICommand<bool>;
internal sealed record SelectPlaybackSource(string? Mode) : ICommand<bool>;
internal sealed record SetMediaControls(bool Enabled) : ICommand<bool>;
// 选择窗口必须携带打开时的曲目身份，不能在提交时改用当前正在播放的曲目。
internal sealed record ApplyLyricsCandidate(LyricsSelectionContext Context, LyricCandidate Candidate) : ICommand<bool>;
internal sealed record SetLyricOffset(LyricsSelectionContext Context, int OffsetMs) : ICommand<bool>;
internal sealed record RestoreAutomaticLyrics(LyricsSelectionContext Context) : ICommand<bool>;
/// <summary>清除歌词缓存和手选记忆，保留独立保存的单曲偏移。</summary>
internal sealed record ClearLyricsData : ICommand<bool>;
/// <summary>展示层解码失败时撤销当前封面，允许同曲重试重新取图。</summary>
internal sealed record RejectCover : ICommand<bool>;
