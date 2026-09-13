using Stanza.Models;
using Stanza.Services.Playback;
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
    public void Track_switch_skips_stale_snapshot_inside_window()
    {
        var tracker = CreateTracker();
        tracker.OnTrackSwitch(10_000);

        var result = tracker.Sample(
            rawPositionMs: 1234,
            snapshotUtcMs: 9_000,
            isPlaying: true,
            nowUtcMs: 10_500);

        Assert.False(result.ShouldRender);
        Assert.Contains("旧快照", result.SkipReason);
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

    private static PlaybackPositionTracker CreateTracker()
    {
        return new PlaybackPositionTracker(
            PlaybackTimingPolicy.Default,
            PlaybackSwitchPolicy.Default);
    }
}
