using System.Collections.Immutable;
using System.Security.Cryptography;

namespace Stanza.Core.Workflows;

/// <summary>管理封面字节、来源替换策略和有限重试；图片解码留在展示层。</summary>
internal sealed class CoverWorkflow(WorkflowTasks tasks)
{
    private IPlaybackSourceAdapter _adapter = PlaybackSourceRegistry.ResolveAdapterByKey(PlaybackSourceIds.AutoFallback);
    private bool _enabled;
    // 同曲也可能启动新一轮重试，仅靠曲目 revision 无法排除上一轮回包。
    private long _generation;
    private string _key = string.Empty;
    private string _fingerprint = string.Empty;
    public ImmutableArray<byte> Data { get; private set; } = [];
    public event Action? Changed;

    public void SetAdapter(IPlaybackSourceAdapter adapter)
    {
        if (ReferenceEquals(_adapter, adapter)) return;
        Clear();
        _adapter = adapter;
    }

    public void SetEnabled(bool enabled)
    {
        if (_enabled == enabled) return;
        _enabled = enabled;
        if (!enabled) Clear();
    }

    public void Clear()
    {
        _generation++;
        _adapter.ResetCoverState(_key);
        _key = string.Empty;
        _fingerprint = string.Empty;
        Data = [];
        Changed?.Invoke();
    }

    /// <summary>解码失败后重置去重与来源缓存，保留当前重试轮次以便再次取图。</summary>
    public void RejectCurrent()
    {
        _adapter.ResetCoverState(_key);
        _fingerprint = string.Empty;
        Data = [];
        Changed?.Invoke();
    }

    public async Task RefreshWithRetryAsync(IPlaybackSession session, PlaybackMedia media, string key,
        long revision, Func<bool> isSessionCurrent, Func<long> currentRevision)
    {
        var generation = ++_generation;
        // 轮次、曲目和会话分别校验，避免开关媒体区或快切后复用旧图片。
        bool IsCurrent() => _enabled && generation == _generation &&
            revision == currentRevision() && isSessionCurrent() && !tasks.Token.IsCancellationRequested;
        await RefreshAsync(media, key, IsCurrent);
        if (string.IsNullOrEmpty(key)) return;
        foreach (var delay in new[] { 150, 350, 800, 1500 })
        {
            await Task.Delay(delay, tasks.Token);
            if (!IsCurrent()) return;
            // 封面可能迟于标题到达，每轮重新读取媒体属性，不能反复读第一份缩略图。
            try { media = await session.ReadMediaAsync(tasks.Token); }
            catch (OperationCanceledException) when (tasks.Token.IsCancellationRequested) { return; }
            catch { return; }
            if (!IsCurrent()) return;
            await RefreshAsync(media, key, IsCurrent);
        }
    }

    private async Task RefreshAsync(PlaybackMedia media, string key, Func<bool> isCurrent)
    {
        if (!isCurrent()) return;
        if (_key != key)
        {
            _key = key;
            _fingerprint = string.Empty;
        }
        try
        {
            var result = await _adapter.GetCoverAsync(
                new PlaybackCoverRequest(key, media, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()), tasks.Token);
            if (!isCurrent() || result.Action == PlaybackCoverAction.NoChange) return;
            if (result.Action == PlaybackCoverAction.NoCover)
            {
                Data = [];
                _fingerprint = string.Empty;
                Changed?.Invoke();
                return;
            }
            if (result.Data is not { Length: > 0 } bytes) return;
            var fingerprint = Convert.ToHexString(SHA256.HashData(bytes));
            if (_fingerprint == fingerprint) return;
            _fingerprint = fingerprint;
            Data = ImmutableArray.CreateRange(bytes);
            Changed?.Invoke();
        }
        catch (OperationCanceledException) when (tasks.Token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!isCurrent()) return;
            _adapter.ResetCoverState(key);
            AppLog.Warn($"封面读取失败 key='{key}': {ex.Message}");
            if (_fingerprint.Length == 0)
            {
                Data = [];
                Changed?.Invoke();
            }
        }
    }
}
