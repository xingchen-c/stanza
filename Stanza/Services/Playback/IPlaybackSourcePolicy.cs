namespace Stanza.Services.Playback;

/// <summary>
///     播放源行为策略：来源识别、元数据规整，以及切歌、时间线和播放能力参数。
///     本接口不执行平台操作；平台操作由 IPlaybackSourceAdapter 承担。
/// </summary>
internal interface IPlaybackSourcePolicy
{
    /// <summary>策略唯一 Key，与 PlaybackSourceIds 对应。</summary>
    string Key { get; }

    /// <summary>展示名称，用于日志和诊断。</summary>
    string DisplayName { get; }

    /// <summary>判断给定 SMTC AUMID 是否属于当前播放源。</summary>
    bool Matches(string? sourceAppUserModelId);

    /// <summary>按播放源规则规整原始元数据。</summary>
    PlaybackMetadata NormalizeMetadata(PlaybackMetadata raw);

    /// <summary>切歌和误判恢复阈值。</summary>
    PlaybackSwitchPolicy Switch { get; }

    /// <summary>时间线可信度与位置合成参数。</summary>
    PlaybackTimingPolicy Timing { get; }

    /// <summary>播放源静态能力缺省值。</summary>
    PlaybackCapabilities Capabilities { get; }
}
