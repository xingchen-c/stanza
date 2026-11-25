using System.Collections.Immutable;

namespace Stanza.Core.ReadModels;

/// <summary>一次完整发布的显示状态；行、逐字和封面集合在发布后保持不变。</summary>
internal sealed record NowPlayingSnapshot(
    bool Connected, NowPlaying? Track, PlaybackState PlaybackState, PlaybackCapabilities Capabilities,
    bool IsSwitchingTrack, PlaybackSample Sample, IReadOnlyList<LyricLine> Lines,
    int LyricsGeneration, int LyricOffsetMs, string Status, ImmutableArray<byte> Cover)
{
    public string CurrentTitle => Track?.Title ?? string.Empty;
    public string CurrentArtist => Track?.Artist ?? string.Empty;
}

/// <summary>界面读取契约，不推进播放状态，也不应用搜索得到的歌词候选。</summary>
internal interface INowPlayingQueries
{
    /// <summary>在状态调度上下文中通知；订阅者只更新展示，关闭窗口时退订。</summary>
    event Action<NowPlayingSnapshot>? Changed;
    NowPlayingSnapshot GetSnapshot();
    /// <summary>固定本次选择的曲目身份；无会话或元数据不可用时返回 null。</summary>
    LyricsSelectionContext? CreateLyricsSelectionContext();
    bool IsLyricsSelectionCurrent(LyricsSelectionContext context);
    /// <summary>搜索期间切歌则丢弃结果；等待同时受窗口取消和应用退出控制。</summary>
    Task<IReadOnlyList<LyricCandidate>> GetLyricCandidatesAsync(LyricsSelectionContext context, CancellationToken cancellationToken = default);
    int GetLyricOffset(LyricsSelectionContext context);
}
