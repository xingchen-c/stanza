using System.Diagnostics;
using Stanza.Models;

namespace Stanza.Services.Lyrics;

/// <summary>歌词加载、候选选择和 NotFound 恢复编排。</summary>
internal sealed class LyricsCoordinator
{
    private const int DefaultRecoveryLifetimeMs = 30_000;
    private const int DefaultIdlePollIntervalMs = 250;
    private static readonly int[] DefaultRecoveryBackoffMs = [100, 300, 500, 700, 900];

    private readonly ILyricsChoiceStore _choiceStore;
    private readonly Func<string> _currentKey;
    private readonly int[] _recoveryBackoffMs;
    private readonly int _recoveryLifetimeMs;
    private readonly int _idlePollIntervalMs;
    private CancellationTokenSource? _recoveryCts;

    public LyricsCoordinator(
        ILyricsCacheStore cacheStore,
        ILyricsChoiceStore choiceStore,
        Func<string> currentKey,
        params ILyricsProvider[] providers)
        : this(
            cacheStore,
            choiceStore,
            currentKey,
            DefaultRecoveryLifetimeMs,
            DefaultIdlePollIntervalMs,
            DefaultRecoveryBackoffMs,
            providers)
    {
    }

    internal LyricsCoordinator(
        ILyricsCacheStore cacheStore,
        ILyricsChoiceStore choiceStore,
        Func<string> currentKey,
        int recoveryLifetimeMs,
        int idlePollIntervalMs,
        int[] recoveryBackoffMs,
        params ILyricsProvider[] providers)
    {
        _choiceStore = choiceStore;
        _currentKey = currentKey;
        _recoveryLifetimeMs = recoveryLifetimeMs;
        _idlePollIntervalMs = idlePollIntervalMs;
        _recoveryBackoffMs = recoveryBackoffMs;
        Lyrics = new LyricsService(cacheStore, providers);
    }

    public LyricsService Lyrics { get; }
    internal Task RecoveryCompletion { get; private set; } = Task.CompletedTask;

    public event Action<string, LyricsLoadResult>? ResultApplied;
    public event Action<string>? StatusChanged;

    public Task<LyricsLoadResult> LoadAsync(LyricsQuery query)
        => LoadAsync(query, Lyrics.Generation);

    private async Task<LyricsLoadResult> LoadAsync(
        LyricsQuery query, int generation, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_choiceStore.TryGet(query.Key, out var choice) && choice is not null)
        {
            // 旧版普通曲目缓存可能仍是错误自动歌词，只信任明确属于此候选的缓存。
            if (await Lyrics.TryLoadCachedAsync(
                    LyricsService.CandidateCacheKey(query.Key, choice), generation, cancellationToken, query.Key) is { } cached)
                return cached;
            if (!Lyrics.IsCurrentGeneration(generation))
                return LyricsLoadResult.NotFound;

            var chosenResult = await Lyrics.LoadCandidateAsync(choice, generation, query.Key, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (chosenResult != LyricsLoadResult.NotFound || !Lyrics.IsCurrentGeneration(generation))
                return chosenResult;
            AppLog.Warn("记忆的歌词候选加载失败，退回自动匹配");
        }

        return await Lyrics.LoadAsync(query, generation, cancellationToken);
    }

    public void ApplyResult(
        string key,
        LyricsLoadResult result,
        bool showNotFound = true)
    {
        ResultApplied?.Invoke(key, result);
        if (showNotFound || result != LyricsLoadResult.NotFound)
            StatusChanged?.Invoke(StatusTextFor(result));
    }

    public Task<IReadOnlyList<LyricCandidate>> GetCandidatesAsync(
        LyricsQuery query, CancellationToken cancellationToken = default)
        => Lyrics.GetCandidatesAsync(query, cancellationToken);

    public async Task<bool> ApplyCandidateAsync(
        string key, LyricCandidate candidate, CancellationToken cancellationToken = default)
    {
        if (_currentKey() != key || cancellationToken.IsCancellationRequested)
            return false;

        CancelRecovery();
        var generation = Lyrics.BeginManualSelection(key);
        StatusChanged?.Invoke("正在应用所选歌词…");
        LyricsLoadResult result;
        try
        {
            result = await Lyrics.LoadCandidateAsync(candidate, generation, key, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }

        // 世代闸门保住了 _lines，但下面每一条副作用都会污染新歌状态。
        if (!IsCurrent(key, generation, cancellationToken))
        {
            AppLog.Debug($"候选加载已过期，丢弃旧候选结果: '{candidate.Title}'");
            return false;
        }

        if (result == LyricsLoadResult.NotFound)
        {
            AppLog.Warn($"候选无歌词: '{candidate.Title}'（来源={candidate.SourceName}）");
            StatusChanged?.Invoke("所选歌词版本没有歌词，请换一个试试");
            return false;
        }

        if (result == LyricsLoadResult.Instrumental)
        {
            AppLog.Warn($"候选为纯音乐: '{candidate.Title}'（来源={candidate.SourceName}）");
            ApplyResult(key, result);
            return true;
        }

        _choiceStore.Put(key, candidate);
        ApplyResult(key, result);
        AppLog.Info(
            $"已应用用户选择的歌词: '{candidate.Title}' / {candidate.Artist}（{result}）");
        return true;
    }

    public void ScheduleRecovery(
        string key,
        LyricsQuery fallbackQuery,
        Func<CancellationToken, Task<RecoveryProbe>> readCurrentQuery)
    {
        CancelRecovery();
        var cts = new CancellationTokenSource();
        _recoveryCts = cts;
        RecoveryCompletion = RunRecoveryAsync(key, fallbackQuery, readCurrentQuery, Lyrics.Generation, cts);
    }

    public void CancelRecovery()
    {
        var cts = _recoveryCts;
        _recoveryCts = null;
        cts?.Cancel();
    }

    public void ClearAll()
    {
        Lyrics.ClearCache();
        _choiceStore.Clear();
    }

    private async Task RunRecoveryAsync(
        string key,
        LyricsQuery fallbackQuery,
        Func<CancellationToken, Task<RecoveryProbe>> readCurrentQuery,
        int generation,
        CancellationTokenSource cts)
    {
        try
        {
            var token = cts.Token;
            var stopwatch = Stopwatch.StartNew();

            await Task.Delay(300, token);
            if (!IsCurrent(key, generation, token))
                return;

            var quickProbe = await readCurrentQuery(token);
            if (!IsCurrent(key, generation, token) || quickProbe.Kind == RecoveryProbeKind.Abort)
                return;

            var quickQuery = quickProbe.Kind == RecoveryProbeKind.Ready
                ? quickProbe.Query!
                : fallbackQuery;
            AppLog.Debug($"NotFound 快速重试: {quickQuery.Title} / {quickQuery.Artist}");
            var quickResult = await LoadAsync(quickQuery, generation, token);
            if (!IsCurrent(key, generation, token))
                return;

            if (quickResult != LyricsLoadResult.NotFound)
            {
                ApplyResult(key, quickResult);
                return;
            }

            ApplyResult(key, LyricsLoadResult.NotFound, showNotFound: false);
            var attempt = 0;
            while (attempt < _recoveryBackoffMs.Length)
            {
                await Task.Delay(_recoveryBackoffMs[attempt], token);
                token.ThrowIfCancellationRequested();

                if (Lyrics.Lines.Count > 0 || !IsCurrent(key, generation, token))
                    return;

                var probe = await readCurrentQuery(token);
                if (!IsCurrent(key, generation, token) || probe.Kind == RecoveryProbeKind.Abort)
                    return;

                if (probe.Kind == RecoveryProbeKind.Retry)
                {
                    if (stopwatch.ElapsedMilliseconds > _recoveryLifetimeMs)
                    {
                        AppLog.Info(
                            $"NotFound 恢复等待超过 {_recoveryLifetimeMs}ms 仍不可读，落地为未找到: key={key}");
                        ApplyResult(key, LyricsLoadResult.NotFound);
                        return;
                    }

                    await Task.Delay(_idlePollIntervalMs, token);
                    if (!IsCurrent(key, generation, token))
                        return;
                    continue;
                }

                attempt++;
                var query = probe.Query!;
                AppLog.Debug($"NotFound 后台补抓第 {attempt} 次: {query.Title} / {query.Artist}");
                var result = await LoadAsync(query, generation, token);
                if (!IsCurrent(key, generation, token))
                    return;

                if (result == LyricsLoadResult.NotFound)
                    continue;

                AppLog.Info($"NotFound 后台补抓得到确定结果: {result}");
                ApplyResult(key, result);
                return;
            }

            if (!IsCurrent(key, generation, token))
                return;
            AppLog.Info($"NotFound 补抓 {_recoveryBackoffMs.Length} 次均失败，确认无歌词");
            ApplyResult(key, LyricsLoadResult.NotFound);
        }
        catch (OperationCanceledException)
        {
            // 切歌/断开/手选导致的正常取消，占位由取消发起方负责重设。
        }
        catch (Exception ex)
        {
            AppLog.Error($"NotFound 恢复调度异常: {ex.Message}");
        }
        finally
        {
            cts.Dispose();
            if (ReferenceEquals(_recoveryCts, cts))
                _recoveryCts = null;
        }
    }

    private bool IsCurrent(string key, int generation, CancellationToken token)
        => !token.IsCancellationRequested && _currentKey() == key && Lyrics.IsCurrentGeneration(generation);

    private static string StatusTextFor(LyricsLoadResult result)
    {
        return result switch
        {
            LyricsLoadResult.Loaded => string.Empty,
            LyricsLoadResult.Instrumental => "此歌曲为没有填词的纯音乐，请您欣赏",
            _ => "未找到歌词"
        };
    }
}
