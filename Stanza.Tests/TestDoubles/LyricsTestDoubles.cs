using Stanza.Models;
using Stanza.Services.Lyrics;

namespace Stanza.Tests.TestDoubles;

/// <summary>带写入记录的歌词持久化缓存替身。</summary>
internal sealed class RecordingLyricsCacheStore : ILyricsCacheStore
{
    private readonly Dictionary<string, (LyricsLoadResult Status, List<LyricLine> Lines)> _items = [];
    private readonly List<string> _writtenKeys = [];

    public IReadOnlyList<string> WrittenKeys => _writtenKeys;

    public void Initialize()
    {
    }

    public bool TryGet(
        string key,
        out LyricsLoadResult status,
        out List<LyricLine> lines)
    {
        if (_items.TryGetValue(key, out var item))
        {
            status = item.Status;
            lines = [.. item.Lines];
            return true;
        }

        status = LyricsLoadResult.NotFound;
        lines = [];
        return false;
    }

    public void Put(
        string key,
        LyricsLoadResult status,
        IReadOnlyList<LyricLine> lines)
    {
        _items[key] = (status, [.. lines]);
        _writtenKeys.Add(key);
    }

    public void Clear()
    {
        _items.Clear();
        _writtenKeys.Clear();
    }
}

/// <summary>带写入记录的歌词选择记忆替身。</summary>
internal sealed class RecordingLyricsChoiceStore : ILyricsChoiceStore
{
    private readonly Dictionary<string, LyricCandidate> _items = [];
    private readonly List<string> _writtenKeys = [];

    public IReadOnlyList<string> WrittenKeys => _writtenKeys;

    public void Initialize()
    {
    }

    public bool TryGet(string key, out LyricCandidate? candidate)
        => _items.TryGetValue(key, out candidate);

    public void Put(string key, LyricCandidate candidate)
    {
        _items[key] = candidate;
        _writtenKeys.Add(key);
    }

    public void Clear()
    {
        _items.Clear();
        _writtenKeys.Clear();
    }
}

/// <summary>可按歌名卡住/放行的歌词源替身。</summary>
internal sealed class GatedLyricsProvider : ILyricsProvider
{
    private readonly Dictionary<string, TaskCompletionSource<LyricsResult>> _gates = [];

    public string Key => "Gated";
    public string Name => "可控测试源";

    public TaskCompletionSource<LyricsResult> Gate(string title)
        => _gates[title] = new TaskCompletionSource<LyricsResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);

    public Task<LyricsResult> LoadAsync(LyricsQuery query)
        => _gates.TryGetValue(query.Title, out var gate)
            ? gate.Task
            : Task.FromResult(new LyricsResult(LyricsLoadResult.NotFound, []));

    public Task<IReadOnlyList<LyricCandidate>> GetCandidatesAsync(LyricsQuery query)
        => Task.FromResult<IReadOnlyList<LyricCandidate>>([
            new(Key, Name, query.Title, query.Title, query.Artist, query.Album, query.DurationMs, null)]);

    public Task<LyricsResult> LoadCandidateAsync(LyricCandidate candidate)
        => _gates.TryGetValue(candidate.Title, out var gate)
            ? gate.Task
            : Task.FromResult(new LyricsResult(LyricsLoadResult.NotFound, []));
}

/// <summary>记录调用次数并按固定结果返回的歌词源替身。</summary>
internal sealed class CountingLyricsProvider(LyricsLoadResult result) : ILyricsProvider
{
    public int LoadCount { get; private set; }

    public string Key => "Counting";
    public string Name => "计数测试源";

    public Task<LyricsResult> LoadAsync(LyricsQuery query)
    {
        LoadCount++;
        return Task.FromResult(new LyricsResult(result, []));
    }

    public Task<LyricsResult> LoadCandidateAsync(LyricCandidate candidate)
    {
        LoadCount++;
        return Task.FromResult(new LyricsResult(result, []));
    }

    public Task<IReadOnlyList<LyricCandidate>> GetCandidatesAsync(LyricsQuery query)
        => Task.FromResult<IReadOnlyList<LyricCandidate>>([
            new(Key, Name, query.Title, query.Title, query.Artist, query.Album, query.DurationMs, null)]);
}
