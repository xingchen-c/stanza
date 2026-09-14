namespace Stanza.Services.Playback;

/// <summary>
///     播放源专属平台操作。当前只承接封面读取；seek/外部位置观测后续可在同一层扩展。
///     适配器决定是否重读、是否允许同一首歌替换封面；公共协调器只负责结果生命周期。
/// </summary>
internal interface IPlaybackSourceAdapter
{
    /// <summary>适配器唯一 Key，与对应 IPlaybackSourcePolicy.Key 保持一致。</summary>
    string Key { get; }

    /// <summary>当前播放源是否声明提供封面。</summary>
    bool ProvidesCover { get; }

    /// <summary>
    ///     获取封面原始图片字节。适配器负责决定是否读取、是否允许同曲替换以及何时重试；
    ///     公共层只负责过期结果检查、图片哈希、解码和发给 UI。
    /// </summary>
    Task<PlaybackCoverResult> GetCoverAsync(
        PlaybackCoverRequest request,
        CancellationToken cancellationToken);

    /// <summary>公共层解码失败或重新启用媒体区时，重置指定曲目的封面缓存状态。</summary>
    void ResetCoverState(string trackKey);
}
