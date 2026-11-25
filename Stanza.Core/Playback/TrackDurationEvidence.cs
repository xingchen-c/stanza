namespace Stanza.Core.Playback;

/// <summary>匹配用时长的曲目归属；无法确认的快照不参与歌词排序。</summary>
internal sealed class TrackDurationEvidence
{
    private string? _key;
    private long _confirmedUtcMs;

    internal int? Read(string key, int? durationMs, long snapshotUtcMs, long nowUtcMs)
    {
        if (_key != key)
        {
            // 首次接入没有上一曲；后续换曲只能信任元数据确认之后发布的时间线。
            var firstTrack = _key is null;
            _key = key;
            _confirmedUtcMs = firstTrack ? 0 : nowUtcMs;
        }
        return durationMs is > 0 && snapshotUtcMs > 0 && snapshotUtcMs >= _confirmedUtcMs
            ? durationMs
            : null;
    }

    internal void Reset()
    {
        _key = null;
        _confirmedUtcMs = 0;
    }
}
