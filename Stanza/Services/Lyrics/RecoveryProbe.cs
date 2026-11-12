using Stanza.Models;

namespace Stanza.Services.Lyrics;

/// <summary>NotFound 恢复循环每轮探测当前曲目的结果类型。</summary>
internal enum RecoveryProbeKind
{
    /// <summary>读到与目标 key 一致的查询，可以补抓。</summary>
    Ready,

    /// <summary>暂时不可读（暂停中 / 元数据未就绪）：跳过本轮，不消耗补抓次数。</summary>
    Retry,

    /// <summary>已切歌或会话断开：终止循环，且不得再改动 UI。</summary>
    Abort
}

/// <summary>一次恢复探测的结果。</summary>
internal readonly record struct RecoveryProbe(RecoveryProbeKind Kind, LyricsQuery? Query)
{
    public static RecoveryProbe Ready(LyricsQuery query) => new(RecoveryProbeKind.Ready, query);
    public static RecoveryProbe Retry() => new(RecoveryProbeKind.Retry, null);
    public static RecoveryProbe Abort() => new(RecoveryProbeKind.Abort, null);
}
