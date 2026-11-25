using Stanza.Core.Workflows;

namespace Stanza.Core.ReadModels;

/// <summary>在状态上下文中组装快照，再整体替换引用，避免界面分别读取到不同批次的数据。</summary>
internal sealed class NowPlayingProjection
{
    private static readonly IReadOnlyList<LyricLine> EmptyLines = Array.AsReadOnly(Array.Empty<LyricLine>());
    private readonly PlaybackContext _playback;
    private readonly LyricsContext _lyrics;
    private readonly ILyricsOffsetStore _offsets;
    private readonly CoverWorkflow _cover;
    private NowPlayingSnapshot _snapshot;

    public NowPlayingProjection(PlaybackContext playback, LyricsContext lyrics, ILyricsOffsetStore offsets, CoverWorkflow cover)
    {
        _playback = playback;
        _lyrics = lyrics;
        _offsets = offsets;
        _cover = cover;
        _snapshot = Build();
    }
    public event Action<NowPlayingSnapshot>? Changed;
    public NowPlayingSnapshot Current => Volatile.Read(ref _snapshot);
    public void Publish()
    {
        var snapshot = Build();
        if (snapshot == _snapshot) return;
        Volatile.Write(ref _snapshot, snapshot);
        Changed?.Invoke(snapshot);
    }
    // 切歌会分步骤更新曲目和歌词；尚未对齐归属时发布空行，不能夹带上一首歌词。
    private NowPlayingSnapshot Build() => new(
        _playback.Connected, _playback.NowPlaying, _playback.PlaybackState, _playback.Capabilities,
        _playback.Switch.IsSwitching, _playback.Sample,
        _lyrics.LinesKey == _playback.CurrentKey && _playback.NowPlaying?.Key == _playback.CurrentKey
            ? _lyrics.Lines : EmptyLines,
        _lyrics.Generation, _offsets.Get(_playback.CurrentKey), _playback.Status, _cover.Data);
}

/// <summary>只读投影和当前选择身份；网络搜索只返回候选，不改变当前歌词。</summary>
internal sealed class NowPlayingQueries(
    NowPlayingProjection projection, PlaybackContext playback, LyricsLoader loader, ILyricsOffsetStore offsets,
    WorkflowTasks tasks) : INowPlayingQueries
{
    public event Action<NowPlayingSnapshot>? Changed
    {
        add => projection.Changed += value;
        remove => projection.Changed -= value;
    }
    public NowPlayingSnapshot GetSnapshot() => projection.Current;
    public LyricsSelectionContext? CreateLyricsSelectionContext() => playback.Requests.CreateContext();
    public bool IsLyricsSelectionCurrent(LyricsSelectionContext context) => playback.Requests.IsSelectionCurrent(context);
    public int GetLyricOffset(LyricsSelectionContext context) => offsets.Get(context.Key);
    public Task<IReadOnlyList<LyricCandidate>> GetLyricCandidatesAsync(LyricsSelectionContext context, CancellationToken cancellationToken = default)
        => tasks.Track(SearchAsync(context, cancellationToken));

    private async Task<IReadOnlyList<LyricCandidate>> SearchAsync(LyricsSelectionContext context, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, tasks.Token);
        linked.Token.ThrowIfCancellationRequested();
        if (!IsLyricsSelectionCurrent(context)) return [];
        var candidates = await loader.GetCandidatesAsync(context.Query, linked.Token);
        linked.Token.ThrowIfCancellationRequested();
        // await 期间可能已切歌，入口校验通过不代表回包仍属于当前选择。
        return IsLyricsSelectionCurrent(context) ? candidates : [];
    }
}
