using System.IO;
using System.Security.Cryptography;

namespace Stanza.Core.Playback;

/// <summary>
///     封面接受策略：默认保留第一张非空封面；网易云配置允许在短时间内替换迟到封面。
/// </summary>
internal sealed class PlaybackCoverAdapter(
    string key,
    bool providesCover,
    bool allowSameTrackReplacement = false,
    int replacementWindowMs = 0,
    int readThrottleMs = 0) : IPlaybackSourceAdapter
{
    private string _trackKey = string.Empty;
    private string? _lastFingerprint;
    private long _firstCoverUtcMs;
    private long _lastReadUtcMs;
    private bool _stable;

    public string Key => key;
    public bool ProvidesCover => providesCover;

    public async Task<PlaybackCoverResult> GetCoverAsync(
        PlaybackCoverRequest request,
        CancellationToken cancellationToken)
    {
        ResetIfTrackChanged(request.TrackKey);

        if (request.Props.ReadCoverAsync is null)
            return _lastFingerprint is null ? PlaybackCoverResult.NoCover : PlaybackCoverResult.NoChange;

        if (!allowSameTrackReplacement && _lastFingerprint is not null)
            return PlaybackCoverResult.NoChange;

        if (_stable)
            return PlaybackCoverResult.NoChange;

        if (readThrottleMs > 0 && _lastReadUtcMs > 0 &&
            request.NowUtcMs - _lastReadUtcMs < readThrottleMs)
            return PlaybackCoverResult.NoChange;

        _lastReadUtcMs = request.NowUtcMs;

        var bytes = await request.Props.ReadCoverAsync(cancellationToken) ?? [];
        cancellationToken.ThrowIfCancellationRequested();
        if (bytes.Length == 0)
            return _lastFingerprint is null ? PlaybackCoverResult.NoCover : PlaybackCoverResult.NoChange;

        var fingerprint = Convert.ToHexString(SHA256.HashData(bytes));
        if (string.Equals(fingerprint, _lastFingerprint, StringComparison.Ordinal))
        {
            MarkStableIfNeeded(request.NowUtcMs);
            return PlaybackCoverResult.NoChange;
        }

        if (_lastFingerprint is null)
            _firstCoverUtcMs = request.NowUtcMs;

        _lastFingerprint = fingerprint;
        MarkStableIfNeeded(request.NowUtcMs);
        return PlaybackCoverResult.Available(bytes);
    }

    public void ResetCoverState(string trackKey)
    {
        if (!string.Equals(trackKey, _trackKey, StringComparison.Ordinal))
            return;

        _lastFingerprint = null;
        _firstCoverUtcMs = 0;
        _lastReadUtcMs = 0;
        _stable = false;
    }

    private void ResetIfTrackChanged(string trackKey)
    {
        if (string.Equals(trackKey, _trackKey, StringComparison.Ordinal))
            return;

        _trackKey = trackKey;
        _lastFingerprint = null;
        _firstCoverUtcMs = 0;
        _lastReadUtcMs = 0;
        _stable = false;
    }

    private void MarkStableIfNeeded(long nowUtcMs)
    {
        if (!allowSameTrackReplacement || replacementWindowMs <= 0) return;
        if (_firstCoverUtcMs > 0 && nowUtcMs - _firstCoverUtcMs > replacementWindowMs)
            _stable = true;
    }
}
