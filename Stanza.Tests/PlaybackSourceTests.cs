using Stanza.Core.Models;
using Stanza.Core.Playback;
using Stanza.Core.Lyrics;
using System.Text.Json;
using Xunit;

namespace Stanza.Tests;

public class PlaybackSourceSelectorTests
{
    [Fact]
    public void Auto_prefers_current_session_over_another_playing_session()
    {
        var sessions = new[]
        {
            Descriptor("current", "app.current", PlaybackState.Paused, true),
            Descriptor("other", "app.other", PlaybackState.Playing, false)
        };

        Assert.Equal(0, PlaybackSourceSelector.SelectIndex(
            sessions,
            PlaybackSourceIds.Auto,
            "current"));
    }

    [Fact]
    public void Auto_without_current_session_prefers_playing_session()
    {
        var sessions = new[]
        {
            Descriptor("paused", "app.paused", PlaybackState.Paused, false),
            Descriptor("playing", "app.playing", PlaybackState.Playing, false)
        };

        Assert.Equal(1, PlaybackSourceSelector.SelectIndex(
            sessions,
            PlaybackSourceIds.Auto,
            null));
    }

    [Fact]
    public void Explicit_qq_does_not_fall_back_to_other_sessions()
    {
        var sessions = new[]
        {
            Descriptor("other", "app.other", PlaybackState.Playing, true)
        };

        Assert.Equal(-1, PlaybackSourceSelector.SelectIndex(
            sessions,
            PlaybackSourceIds.QqMusic,
            "other"));
    }

    [Fact]
    public void Explicit_qq_selects_observed_qqmusic_session()
    {
        var sessions = new[]
        {
            Descriptor("other", "app.other", PlaybackState.Playing, true),
            Descriptor("qq", "QQMusic.exe", PlaybackState.Paused, false)
        };

        Assert.Equal(1, PlaybackSourceSelector.SelectIndex(
            sessions,
            PlaybackSourceIds.QqMusic,
            "other"));
    }

    [Fact]
    public void Explicit_generic_selects_unmatched_playing_session()
    {
        var sessions = new[]
        {
            Descriptor("paused", "app.paused", PlaybackState.Paused, false),
            Descriptor("playing", "app.playing", PlaybackState.Playing, false)
        };

        Assert.Equal(1, PlaybackSourceSelector.SelectIndex(
            sessions,
            PlaybackSourceIds.AutoFallback,
            null));
    }

    [Fact]
    public void Explicit_netease_selects_observed_cloudmusic_session()
    {
        var sessions = new[]
        {
            Descriptor("qq", "QQMusic.exe", PlaybackState.Playing, true),
            Descriptor("netease", "cloudmusic.exe", PlaybackState.Paused, false)
        };

        Assert.Equal(1, PlaybackSourceSelector.SelectIndex(
            sessions,
            PlaybackSourceIds.NetEase,
            "qq"));
    }

    [Fact]
    public void Unknown_explicit_source_returns_no_session()
    {
        var sessions = new[]
        {
            Descriptor("playing", "app.playing", PlaybackState.Playing, true)
        };

        Assert.Equal(-1, PlaybackSourceSelector.SelectIndex(
            sessions,
            "UnknownSource",
            "playing"));
    }

    private static PlaybackSessionDescriptor Descriptor(
        string id,
        string aumid,
        PlaybackState state,
        bool current)
    {
        return new PlaybackSessionDescriptor(id, aumid, state, current);
    }
}

public class PlaybackSourcePolicyTests
{
    [Fact]
    public void Registry_resolves_known_key_case_insensitively()
    {
        Assert.Equal(
            PlaybackSourceIds.QqMusic,
            PlaybackSourceRegistry.ResolveByKey("qq").Key);
    }

    [Fact]
    public void Registry_uses_generic_fallback_for_unknown_session()
    {
        Assert.Equal(
            PlaybackSourceIds.AutoFallback,
            PlaybackSourceRegistry.ResolveBySession("unknown.app").Key);
    }

    [Fact]
    public void Registry_resolves_observed_qqmusic_aumid()
    {
        Assert.Equal(
            PlaybackSourceIds.QqMusic,
            PlaybackSourceRegistry.ResolveBySession("QQMusic.exe").Key);
    }

    [Fact]
    public void Registry_resolves_observed_cloudmusic_aumid()
    {
        Assert.Equal(
            PlaybackSourceIds.NetEase,
            PlaybackSourceRegistry.ResolveBySession("cloudmusic.exe").Key);
    }

    [Fact]
    public void Netease_policy_uses_wall_clock_and_ignores_degenerate_timeline()
    {
        var policy = PlaybackSourceRegistry.ResolveByKey(PlaybackSourceIds.NetEase);

        Assert.False(policy.Timing.TrustLastUpdatedTime);
        Assert.False(policy.Timing.AllowFreshSnapshotExtrapolation);
        Assert.True(policy.Timing.AllowWallClockFallback);
        Assert.False(policy.Timing.TrustPositionWhenPaused);
        Assert.Equal(0, policy.Switch.StaleSnapshotWindowMs);
        Assert.False(policy.Switch.UseStoppedAsEarlySignal);
        Assert.False(policy.Switch.UseChangingAsEarlySignal);
        Assert.False(policy.Switch.UseDurationChangeAsEarlySignal);
        Assert.False(policy.Switch.UsePositionRegressionAsEarlySignal);
    }

    [Fact]
    public void Registry_resolves_adapter_for_each_known_source()
    {
        Assert.Equal(
            PlaybackSourceIds.QqMusic,
            PlaybackSourceRegistry.ResolveAdapterBySession("QQMusic.exe").Key);
        Assert.Equal(
            PlaybackSourceIds.NetEase,
            PlaybackSourceRegistry.ResolveAdapterBySession("cloudmusic.exe").Key);
        Assert.Equal(
            PlaybackSourceIds.AutoFallback,
            PlaybackSourceRegistry.ResolveAdapterBySession("unknown.exe").Key);
    }

    [Fact]
    public void Qq_metadata_normalization_trims_values()
    {
        var policy = PlaybackSourceRegistry.ResolveByKey(PlaybackSourceIds.QqMusic);

        var normalized = policy.NormalizeMetadata(new PlaybackMetadata(
            " title ",
            " artist ",
            " album ",
            1234));

        Assert.Equal("title", normalized.Title);
        Assert.Equal("artist", normalized.Artist);
        Assert.Equal("album", normalized.Album);
        Assert.Equal(1234, normalized.DurationMs);
    }
}

public class TrackSwitchStateMachineTests
{
    [Theory]
    [InlineData(-1000000, -991000)]
    [InlineData(-1, 8999)]
    [InlineData(0, 9000)]
    [InlineData(2147480000, -2147478296)]
    public void 切歌计时支持负数零值和溢出(int startTick, int nowTick)
    {
        var fsm = new TrackSwitchStateMachine();
        Assert.Equal(0, fsm.ElapsedMs(nowTick));
        fsm.Begin("当前歌曲", startTick);
        Assert.Equal(9000, fsm.ElapsedMs(nowTick));
        fsm.Reset();
        Assert.Equal(0, fsm.ElapsedMs(nowTick));
    }

    [Theory]
    [InlineData(-1000000, -999000)]
    [InlineData(-1, 999)]
    [InlineData(0, 1000)]
    [InlineData(2147483000, -2147483296)]
    public void 最近切歌窗口支持负数零值和溢出(int completedTick, int nowTick)
    {
        var fsm = new TrackSwitchStateMachine();
        Assert.False(fsm.RecentlyCompleted(nowTick, 2000));
        fsm.Complete(completedTick);
        Assert.True(fsm.RecentlyCompleted(nowTick, 2000));
        Assert.False(fsm.RecentlyCompleted(unchecked(nowTick + 1000), 2000));
        fsm.Reset();
        Assert.False(fsm.RecentlyCompleted(nowTick, 2000));
    }

    [Fact]
    public void Suspicion_can_be_confirmed_or_cleared()
    {
        var fsm = new TrackSwitchStateMachine();

        fsm.Suspect();
        Assert.Equal(TrackSwitchState.Suspected, fsm.State);
        Assert.True(fsm.IsSuspected);

        fsm.ClearSuspicion();
        Assert.Equal(TrackSwitchState.Idle, fsm.State);

        fsm.Suspect();
        fsm.Begin("old-key", nowTick: 1000);
        Assert.Equal(TrackSwitchState.Switching, fsm.State);
        Assert.True(fsm.IsSwitching);
    }

    [Fact]
    public void Loading_and_completion_update_recent_switch_window()
    {
        var fsm = new TrackSwitchStateMachine();
        fsm.Begin("old-key", nowTick: 1000);
        fsm.MarkLoading();

        Assert.Equal(TrackSwitchState.Loading, fsm.State);
        Assert.Equal(500, fsm.ElapsedMs(1500));

        fsm.Complete(2000);

        Assert.Equal(TrackSwitchState.Idle, fsm.State);
        Assert.True(fsm.RecentlyCompleted(2500, 1000));
        Assert.False(fsm.RecentlyCompleted(3500, 1000));
    }

    [Fact]
    public void Reset_clears_switch_and_completion_state()
    {
        var fsm = new TrackSwitchStateMachine();
        fsm.Begin("old-key", 1000);
        fsm.Complete(2000);

        fsm.Reset();

        Assert.Equal(TrackSwitchState.Idle, fsm.State);
        Assert.Equal(string.Empty, fsm.SwitchKey);
        Assert.False(fsm.RecentlyCompleted(2000, 1000));
        Assert.False(fsm.IsSwitching);
    }
}

public class PlaybackPositionTrackerTests
{
    [Fact]
    public void Fresh_snapshot_extrapolates_to_now()
    {
        var tracker = CreateTracker();

        var result = tracker.Sample(
            rawPositionMs: 1000,
            snapshotUtcMs: 10_000,
            isPlaying: true,
            nowUtcMs: 10_500);

        Assert.True(result.ShouldRender);
        Assert.Equal(1500, result.PositionMs);
    }

    [Fact]
    public void Track_switch_uses_wall_clock_when_new_track_is_playing()
    {
        var tracker = CreateTracker();
        tracker.OnTrackSwitch(10_000);
        tracker.OnTrackConfirmed();

        var result = tracker.Sample(
            rawPositionMs: 1234,
            snapshotUtcMs: 9_000,
            isPlaying: true,
            nowUtcMs: 10_500);

        Assert.True(result.ShouldRender);
        Assert.Equal(500, result.PositionMs);
    }

    [Fact]
    public void 未确认新歌时切歌旧快照应冻结而非推回开头()
    {
        var tracker = CreateTracker();
        tracker.OnTrackSwitch(10_000);

        var result = tracker.Sample(
            rawPositionMs: 200_000,
            snapshotUtcMs: 9_200,
            isPlaying: true,
            nowUtcMs: 10_500);

        Assert.False(result.ShouldRender);
        Assert.Contains("旧快照", result.SkipReason);
    }

    [Fact]
    public void 确认新歌后切歌旧快照应改用墙钟()
    {
        var tracker = CreateTracker();
        tracker.OnTrackSwitch(10_000);
        tracker.OnTrackConfirmed();

        var result = tracker.Sample(
            rawPositionMs: 200_000,
            snapshotUtcMs: 9_200,
            isPlaying: true,
            nowUtcMs: 10_500);

        Assert.True(result.ShouldRender);
        Assert.Equal(500, result.PositionMs);
    }

    [Fact]
    public void 恢复播放边界不得清除切歌旧快照闸门()
    {
        var tracker = CreateTracker();
        tracker.OnTrackSwitch(10_000);
        tracker.OnResumeBoundary(10_200);

        var result = tracker.Sample(
            rawPositionMs: 200_000,
            snapshotUtcMs: 9_200,
            isPlaying: true,
            nowUtcMs: 10_200);

        Assert.NotEqual(200_000, result.PositionMs);
    }

    [Fact]
    public void 恢复当拍应落到锚点而非原始快照位置()
    {
        var tracker = CreateTracker();
        tracker.OnTrackSwitch(10_000, initialPositionMs: 45_000);
        tracker.Sample(45_000, 10_050, isPlaying: true, nowUtcMs: 10_100);
        tracker.Sample(45_100, 10_150, isPlaying: false, nowUtcMs: 10_200);
        tracker.OnResumeBoundary(15_000);

        var same = tracker.Sample(45_100, 10_150, isPlaying: true, nowUtcMs: 15_000);
        Assert.Equal(45_100, same.PositionMs);

        var later = tracker.Sample(45_100, 10_150, isPlaying: true, nowUtcMs: 15_500);
        Assert.Equal(45_600, later.PositionMs);
    }

    [Fact]
    public void Initial_session_uses_current_position_instead_of_wall_clock_from_zero()
    {
        var tracker = CreateTracker();
        tracker.OnTrackSwitch(10_000, initialPositionMs: 60_000);

        var result = tracker.Sample(
            rawPositionMs: 60_000,
            snapshotUtcMs: 9_000,
            isPlaying: true,
            nowUtcMs: 10_500);

        Assert.True(result.ShouldRender);
        Assert.Equal(60_500, result.PositionMs);
    }

    [Fact]
    public void Paused_small_position_drift_is_ignored()
    {
        var tracker = CreateTracker();
        tracker.Sample(5000, 10_000, isPlaying: false, nowUtcMs: 10_000);

        var result = tracker.Sample(
            rawPositionMs: 4500,
            snapshotUtcMs: 10_100,
            isPlaying: false,
            nowUtcMs: 10_100);

        Assert.Equal(5000, result.PositionMs);
    }

    [Fact]
    public void Paused_large_seek_is_accepted()
    {
        var tracker = CreateTracker();
        tracker.Sample(5000, 10_000, isPlaying: false, nowUtcMs: 10_000);

        var result = tracker.Sample(
            rawPositionMs: 9000,
            snapshotUtcMs: 10_200,
            isPlaying: false,
            nowUtcMs: 10_200);

        Assert.Equal(9000, result.PositionMs);
    }

    [Fact]
    public void Resume_continues_from_paused_position_before_new_snapshot()
    {
        var tracker = CreateTracker();
        tracker.OnTrackSwitch(10_000);
        tracker.Sample(5000, 10_000, isPlaying: true, nowUtcMs: 10_100);
        tracker.Sample(5000, 10_000, isPlaying: false, nowUtcMs: 10_200);
        tracker.OnResumeBoundary(10_300);

        var result = tracker.Sample(5000, 10_000, isPlaying: true, nowUtcMs: 10_800);

        Assert.True(result.ShouldRender);
        Assert.Equal(5600, result.PositionMs);
    }

    [Fact]
    public void Netease_track_switch_starts_wall_clock_position_immediately()
    {
        var policy = PlaybackSourceRegistry.ResolveByKey(PlaybackSourceIds.NetEase);
        var tracker = new PlaybackPositionTracker(policy.Timing, policy.Switch);
        tracker.OnTrackSwitch(10_000);
        tracker.OnTrackConfirmed();

        var result = tracker.Sample(
            rawPositionMs: 0,
            snapshotUtcMs: DateTimeOffset.MinValue.ToUnixTimeMilliseconds(),
            isPlaying: true,
            nowUtcMs: 10_500);

        Assert.True(result.ShouldRender);
        Assert.Equal(500, result.PositionMs);
    }

    [Fact]
    public void Netease_pause_freezes_last_computed_position_instead_of_raw_zero()
    {
        var policy = PlaybackSourceRegistry.ResolveByKey(PlaybackSourceIds.NetEase);
        var tracker = new PlaybackPositionTracker(policy.Timing, policy.Switch);
        tracker.OnTrackSwitch(9_000);
        tracker.Sample(
            rawPositionMs: 0,
            snapshotUtcMs: DateTimeOffset.MinValue.ToUnixTimeMilliseconds(),
            isPlaying: true,
            nowUtcMs: 10_000);

        var paused = tracker.Sample(
            rawPositionMs: 0,
            snapshotUtcMs: DateTimeOffset.MinValue.ToUnixTimeMilliseconds(),
            isPlaying: false,
            nowUtcMs: 10_100);

        Assert.Equal(1000, paused.PositionMs);
    }

    private static PlaybackPositionTracker CreateTracker()
    {
        return new PlaybackPositionTracker(
            PlaybackTimingPolicy.Default,
            PlaybackSwitchPolicy.Default);
    }
}

public class LyricCandidateTests
{
    [Fact]
    public void Old_choice_json_remains_deserializable()
    {
        const string json = """
            {
              "SourceKey": "QQ",
              "SourceName": "QQ 音乐",
              "Id": "123",
              "Title": "测试",
              "Artist": "歌手",
              "Album": null,
              "DurationMs": 1000,
              "MatchType": "Perfect"
            }
            """;

        var candidate = JsonSerializer.Deserialize<LyricCandidate>(json);

        Assert.NotNull(candidate);
        Assert.Equal("123", candidate.Id);
        Assert.Equal(0, candidate.MatchScore);
        Assert.Null(candidate.ArtistMatched);
    }

    [Fact]
    public async Task Candidate_list_puts_artist_match_before_higher_scored_mismatch()
    {
        var service = new LyricsLoader(
            new MemoryLyricsCacheStore(),
            new FakeCandidateProvider());

        var candidates = await service.GetCandidatesAsync(
            new LyricsQuery("歌曲", "正确歌手", 1000));

        Assert.Equal("正确歌手", candidates[0].Artist);
        Assert.True(candidates[0].ArtistMatched);
        Assert.Equal("错误歌手", candidates[1].Artist);
        Assert.False(candidates[1].ArtistMatched);
    }

    private sealed class FakeCandidateProvider : ILyricsProvider
    {
        public string Key => "Fake";
        public string Name => "测试源";

        public Task<LyricsResult> LoadAsync(LyricsQuery query)
            => Task.FromResult(new LyricsResult(LyricsLoadResult.NotFound, []));

        public Task<IReadOnlyList<LyricCandidate>> GetCandidatesAsync(
            LyricsQuery query)
        {
            IReadOnlyList<LyricCandidate> result =
            [
                new(Key, Name, "wrong", query.Title, "错误歌手", null, null, "Perfect")
                {
                    MatchScore = 0.95,
                    ArtistMatched = false
                },
                new(Key, Name, "right", query.Title, "正确歌手", null, null, "Low")
                {
                    MatchScore = 0.50,
                    ArtistMatched = true
                }
            ];
            return Task.FromResult(result);
        }
    }

    private sealed class MemoryLyricsCacheStore : ILyricsCacheStore
    {
        private readonly Dictionary<string, (LyricsLoadResult Status, List<LyricLine> Lines)> _items = [];

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
        }

        public void Clear() => _items.Clear();
        public void Remove(string key) => _items.Remove(key);
    }
}
