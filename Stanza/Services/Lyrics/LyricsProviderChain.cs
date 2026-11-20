using System.Diagnostics;
using Stanza.Models;

namespace Stanza.Services.Lyrics;

/// <summary>有界多源搜索、统一决策和按候选取词；注册顺序只作最终平局兜底。</summary>
internal sealed class LyricsProviderChain
{
    private readonly TimeSpan _providerBudget;
    private readonly TimeSpan _totalBudget;
    private readonly ILyricsProvider[] _providers;

    public LyricsProviderChain(params ILyricsProvider[] providers)
        : this(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(8), providers)
    {
    }

    internal LyricsProviderChain(TimeSpan providerBudget, TimeSpan totalBudget, params ILyricsProvider[] providers)
    {
        _providerBudget = providerBudget;
        _totalBudget = totalBudget;
        _providers = providers.Length > 0 ? providers : [new QqMusicLyricsProvider()];
    }

    public async Task<LyricsResult> LoadAsync(LyricsQuery query, CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var ranked = await GetCandidatesAsync(query, cancellationToken);
        var decision = LyricsCandidateRanker.Decide(ranked);
        AppLog.Info($"歌词决策: '{query.Title}' / {query.Artist} 时长={query.DurationMs?.ToString() ?? "?"}ms " +
                    $"候选={ranked.Count} 结果={decision.Status} 偏好={query.PreferredSourceKey ?? "中立"}");
        if (decision.Status != LyricsLoadResult.Loaded)
            return new LyricsResult(decision.Status, []);

        var loaded = new List<LyricsResult>();
        LyricsResult? instrumental = null;
        foreach (var candidate in decision.Candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var budget = RemainingBudget(stopwatch);
            if (budget <= TimeSpan.Zero)
                break;
            var result = await LoadCandidateAsync(candidate, budget, cancellationToken);
            if (result.Status == LyricsLoadResult.NotFound)
                continue;
            result = result with
            {
                Decision = new(LyricsCandidateRanker.AlgorithmVersion, LyricsCandidateRanker.Fingerprint(query), candidate)
            };
            if (result.Status == LyricsLoadResult.Instrumental)
            {
                instrumental ??= result;
                continue;
            }
            loaded.Add(result);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var selected = loaded.OrderByDescending(r => LyricsCandidateRanker.ConfidenceBand(r.Decision!.Candidate))
            .ThenByDescending(r => r.Decision!.Candidate.PreferredSource)
            .ThenByDescending(r => r.Lines.Any(l => l.Words is { Count: > 0 }))
            .ThenByDescending(r => r.Lines.Any(l => !string.IsNullOrWhiteSpace(l.Translation)))
            .FirstOrDefault() ?? instrumental ?? new LyricsResult(LyricsLoadResult.NotFound, []);
        if (selected.Decision is { } evidence)
            AppLog.Info($"歌词采用: 来源={evidence.Candidate.SourceKey} ID={evidence.Candidate.Id} " +
                        $"匹配={evidence.Candidate.MatchType} 同平台={evidence.Candidate.PreferredSource} " +
                        $"结果={selected.Status} 行数={selected.Lines.Count} 耗时={stopwatch.ElapsedMilliseconds}ms");
        return selected;
    }

    public async Task<IReadOnlyList<LyricCandidate>> GetCandidatesAsync(
        LyricsQuery query, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var budget = _providerBudget < _totalBudget ? _providerBudget : _totalBudget;
        // 只并行搜索；等搜索预算结束后统一排序，再串行按候选取词。
        var searches = _providers.Select(async provider =>
        {
            try
            {
                return await WithinBudgetAsync(provider.GetCandidatesAsync(query), budget, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                AppLog.Warn($"候选获取失败 {provider.Name}: {ex.Message}");
                return (IReadOnlyList<LyricCandidate>)[];
            }
        });
        var results = await Task.WhenAll(searches);
        cancellationToken.ThrowIfCancellationRequested();
        return LyricsCandidateRanker.Rank(results.SelectMany(x => x), query);
    }

    public async Task<LyricsResult> LoadCandidateAsync(
        LyricCandidate candidate,
        CancellationToken cancellationToken = default)
    {
        var budget = _providerBudget < _totalBudget ? _providerBudget : _totalBudget;
        return await LoadCandidateAsync(candidate, budget, cancellationToken);
    }

    private async Task<LyricsResult> LoadCandidateAsync(
        LyricCandidate candidate, TimeSpan budget, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var provider = _providers.FirstOrDefault(p => p.Key == candidate.SourceKey);
        if (provider is null)
            return new LyricsResult(LyricsLoadResult.NotFound, []);

        try
        {
            return await WithinBudgetAsync(provider.LoadCandidateAsync(candidate), budget, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppLog.Error($"按候选取词失败 {provider.Name}", ex);
            return new LyricsResult(LyricsLoadResult.NotFound, []);
        }
    }

    private TimeSpan RemainingBudget(Stopwatch stopwatch)
    {
        var remaining = _totalBudget - stopwatch.Elapsed;
        return remaining < _providerBudget ? remaining : _providerBudget;
    }

    private static async Task<T> WithinBudgetAsync<T>(Task<T> task, TimeSpan budget, CancellationToken token)
    {
        try
        {
            return await task.WaitAsync(budget, token);
        }
        finally
        {
            // provider 接口不接受取消：超时/取消只停止等待，观察迟到异常但不让它修改当前流程。
            _ = task.ContinueWith(t => { _ = t.Exception; }, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }
}
