using Stanza.Models;
using Stanza.Services.Lyrics;
using Stanza.Tests.TestDoubles;
using Xunit;

namespace Stanza.Tests;

public class LyricsGenerationTests
{
    [Fact]
    public async Task 迟到的自动加载不得覆盖新歌歌词行()
    {
        var store = new RecordingLyricsCacheStore();
        var provider = new GatedLyricsProvider();
        var service = new LyricsService(store, provider);

        var oldQuery = new LyricsQuery("旧歌", "甲", null);
        var oldGate = provider.Gate(oldQuery.Title);
        var oldGeneration = service.Generation;
        var oldLoad = service.LoadAsync(oldQuery, oldGeneration);

        service.InvalidateInFlight();

        var newQuery = new LyricsQuery("新歌", "乙", null);
        var newGate = provider.Gate(newQuery.Title);
        var newLoad = service.LoadAsync(newQuery, service.Generation);
        newGate.SetResult(new LyricsResult(
            LyricsLoadResult.Loaded,
            [new LyricLine(0, "新歌第一行")]));

        Assert.Equal(LyricsLoadResult.Loaded, await newLoad);
        Assert.Equal("新歌第一行", service.Lines.Single().Text);

        oldGate.SetResult(new LyricsResult(
            LyricsLoadResult.Loaded,
            [new LyricLine(0, "旧歌第一行")]));
        await oldLoad;

        Assert.Equal("新歌第一行", service.Lines.Single().Text);
    }

    [Fact]
    public async Task 迟到的自动加载仍应写入持久化缓存()
    {
        var store = new RecordingLyricsCacheStore();
        var provider = new GatedLyricsProvider();
        var service = new LyricsService(store, provider);
        var oldQuery = new LyricsQuery("旧歌", "甲", null);

        var oldGate = provider.Gate(oldQuery.Title);
        var oldLoad = service.LoadAsync(oldQuery, service.Generation);
        service.InvalidateInFlight();

        var newQuery = new LyricsQuery("新歌", "乙", null);
        var newGate = provider.Gate(newQuery.Title);
        var newLoad = service.LoadAsync(newQuery, service.Generation);
        newGate.SetResult(new LyricsResult(
            LyricsLoadResult.Loaded,
            [new LyricLine(0, "新歌第一行")]));
        await newLoad;

        oldGate.SetResult(new LyricsResult(
            LyricsLoadResult.Loaded,
            [new LyricLine(0, "旧歌第一行")]));
        await oldLoad;

        Assert.Contains(LyricsService.AutomaticCacheKey(oldQuery), store.WrittenKeys);
        Assert.True(store.TryGet(LyricsService.AutomaticCacheKey(oldQuery), out var status, out var lines));
        Assert.Equal(LyricsLoadResult.Loaded, status);
        Assert.Equal("旧歌第一行", lines.Single().Text);
    }

    [Fact]
    public async Task 迟到的候选加载不得覆盖新歌歌词行()
    {
        var store = new RecordingLyricsCacheStore();
        var provider = new GatedLyricsProvider();
        var service = new LyricsService(store, provider);
        var candidate = new LyricCandidate(
            provider.Key,
            provider.Name,
            "old-id",
            "旧候选",
            "甲",
            null,
            null,
            null);

        var oldGate = provider.Gate(candidate.Title);
        var oldLoad = service.LoadCandidateAsync(candidate, service.Generation);
        service.InvalidateInFlight();

        var newQuery = new LyricsQuery("新歌", "乙", null);
        var newGate = provider.Gate(newQuery.Title);
        var newLoad = service.LoadAsync(newQuery, service.Generation);
        newGate.SetResult(new LyricsResult(
            LyricsLoadResult.Loaded,
            [new LyricLine(0, "新歌第一行")]));
        await newLoad;

        oldGate.SetResult(new LyricsResult(
            LyricsLoadResult.Loaded,
            [new LyricLine(0, "旧候选第一行")]));
        await oldLoad;

        Assert.Equal("新歌第一行", service.Lines.Single().Text);
    }

    [Fact]
    public async Task 世代已失效时缓存命中不得覆盖歌词行()
    {
        var store = new RecordingLyricsCacheStore();
        var provider = new GatedLyricsProvider();
        var service = new LyricsService(store, provider);
        var oldQuery = new LyricsQuery("旧歌", "甲", null);
        store.Put(
            LyricsService.AutomaticCacheKey(oldQuery),
            LyricsLoadResult.Loaded,
            [new LyricLine(0, "旧歌缓存行")]);
        var staleGeneration = service.Generation;

        service.InvalidateInFlight();

        var newQuery = new LyricsQuery("新歌", "乙", null);
        var newGate = provider.Gate(newQuery.Title);
        var newLoad = service.LoadAsync(newQuery, service.Generation);
        newGate.SetResult(new LyricsResult(
            LyricsLoadResult.Loaded,
            [new LyricLine(0, "新歌第一行")]));
        await newLoad;

        var staleResult = await service.LoadAsync(oldQuery, staleGeneration);

        Assert.Equal(LyricsLoadResult.Loaded, staleResult);
        Assert.Equal("新歌第一行", service.Lines.Single().Text);
    }
}
