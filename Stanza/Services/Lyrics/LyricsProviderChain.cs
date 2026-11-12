using System.Diagnostics;
using Stanza.Models;

namespace Stanza.Services.Lyrics;

/// <summary>按配置顺序执行歌词源，并提供每源与整链超时预算。</summary>
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
        foreach (var provider in _providers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var budget = RemainingBudget(stopwatch);
            if (budget <= TimeSpan.Zero)
                break;

            try
            {
                AppLog.Debug($"尝试歌词源 {provider.Name}: '{query.Title}' / {query.Artist}");
                var result = await WithinBudgetAsync(provider.LoadAsync(query), budget, cancellationToken);
                if (result.Status == LyricsLoadResult.NotFound)
                    continue;

                AppLog.Info(
                    $"歌词加载成功: 来源={provider.Name} 结果={result.Status} " +
                    $"行数={result.Lines.Count} 歌曲='{query.Title}' / {query.Artist}");
                return result;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (TimeoutException)
            {
                AppLog.Warn($"歌词源 {provider.Name} 超过 {budget.TotalSeconds:F1}s 未返回，转下一个源");
            }
            catch (Exception ex)
            {
                AppLog.Error($"歌词源 {provider.Name} 加载异常", ex);
            }
        }

        AppLog.Warn($"所有歌词源均未找到歌词: '{query.Title}' / {query.Artist}");
        return new LyricsResult(LyricsLoadResult.NotFound, []);
    }

    public async Task<IReadOnlyList<LyricCandidate>> GetCandidatesAsync(
        LyricsQuery query,
        CancellationToken cancellationToken = default)
    {
        var list = new List<LyricCandidate>();
        var stopwatch = Stopwatch.StartNew();
        foreach (var provider in _providers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var budget = RemainingBudget(stopwatch);
            if (budget <= TimeSpan.Zero)
                break;

            try
            {
                list.AddRange(await WithinBudgetAsync(provider.GetCandidatesAsync(query), budget, cancellationToken));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (TimeoutException)
            {
                AppLog.Warn($"候选获取超时 {provider.Name}，保留已取得候选并继续");
            }
            catch (Exception ex)
            {
                AppLog.Error($"候选获取失败 {provider.Name}", ex);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        return list
            .Select((candidate, index) => (Candidate: candidate with
            {
                TitleMatched = QqMusicLyricsProvider.IsSameSongTitle(candidate.Title, query.Title)
            }, Index: index))
            .OrderByDescending(x => x.Candidate.TitleMatched)
            .ThenByDescending(x => x.Candidate.ArtistMatched switch
            {
                true => 2,
                null => 1,
                false => 0
            })
            .ThenByDescending(x => x.Candidate.MatchScore)
            .ThenBy(x => x.Index)
            .Select(x => x.Candidate)
            .ToList();
    }

    public async Task<LyricsResult> LoadCandidateAsync(
        LyricCandidate candidate,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var provider = _providers.FirstOrDefault(p => p.Key == candidate.SourceKey);
        if (provider is null)
            return new LyricsResult(LyricsLoadResult.NotFound, []);

        try
        {
            var budget = _providerBudget < _totalBudget ? _providerBudget : _totalBudget;
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
