using System.IO;
using Microsoft.Data.Sqlite;
using Stanza.Models;
using Stanza.Services.Lyrics;
using Stanza.Services.Storage;
using Xunit;

namespace Stanza.Tests;

[CollectionDefinition("SqliteStorage", DisableParallelization = true)]
public sealed class SqliteStorageCollection;

[Collection("SqliteStorage")]
public sealed class SqliteStorageTests(SqliteStorageFixture fixture)
    : IClassFixture<SqliteStorageFixture>
{
    [Fact]
    public void Lyrics_cache_round_trips_and_clears()
    {
        const string key = "sqlite-test:lyrics";
        var lines = new List<LyricLine>
        {
            new(1000, "第一句", 2000, "First", [new LyricWord("第", 1000, 1200)])
        };

        fixture.Cache.Put(key, LyricsLoadResult.Loaded, lines);

        Assert.True(fixture.Cache.TryGet(key, out var status, out var loaded));
        Assert.Equal(LyricsLoadResult.Loaded, status);
        Assert.Single(loaded);
        Assert.Equal("第一句", loaded[0].Text);
        Assert.Equal("First", loaded[0].Translation);
        Assert.Single(loaded[0].Words!);

        fixture.Cache.Clear();
        Assert.False(fixture.Cache.TryGet(key, out _, out _));
    }

    [Fact]
    public void Lyrics_choice_round_trips_and_clears()
    {
        const string key = "sqlite-test:choice";
        var candidate = new LyricCandidate(
            "QQ",
            "QQ 音乐",
            "123",
            "测试歌曲",
            "测试歌手",
            "测试专辑",
            180000,
            "Perfect")
        {
            MatchScore = 0.95,
            ArtistMatched = true
        };

        fixture.Choices.Put(key, candidate);

        Assert.True(fixture.Choices.TryGet(key, out var loaded));
        Assert.Equal(candidate, loaded);

        fixture.Choices.Clear();
        Assert.False(fixture.Choices.TryGet(key, out _));
    }

    [Fact]
    public void 单曲选择删除后重启不恢复且其他歌曲记忆保留()
    {
        var candidate = new LyricCandidate("QQ", "QQ 音乐", "123", "歌名", "歌手", null, null, null);
        fixture.Choices.Put("remove:A", candidate);
        fixture.Choices.Put("remove:B", candidate);

        fixture.Choices.Remove("remove:A");
        var restarted = new SqliteLyricsChoiceStore(fixture.Database);

        Assert.False(restarted.TryGet("remove:A", out _));
        Assert.True(restarted.TryGet("remove:B", out var remaining));
        Assert.Equal(candidate, remaining);
    }

    [Fact]
    public void 单曲偏移重启保留且归零和清缓存不影响其他歌曲()
    {
        var offsets = new SqliteLyricsOffsetStore(fixture.Database);
        offsets.Initialize();
        Assert.Equal(0, offsets.Get("offset:unknown"));
        Assert.True(offsets.Set("offset:A", 500));
        Assert.True(offsets.Set("offset:B", -300));
        fixture.Cache.Clear();
        fixture.Choices.Clear();

        var restarted = new SqliteLyricsOffsetStore(fixture.Database);
        restarted.Initialize();
        Assert.Equal(500, restarted.Get("offset:A"));
        Assert.Equal(-300, restarted.Get("offset:B"));
        Assert.True(restarted.Set("offset:A", 0));

        var afterReset = new SqliteLyricsOffsetStore(fixture.Database);
        afterReset.Initialize();
        Assert.Equal(0, afterReset.Get("offset:A"));
        Assert.Equal(-300, afterReset.Get("offset:B"));
    }

    [Fact]
    public void Database_creates_expected_tables()
    {
        using var connection = fixture.Database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT name FROM sqlite_master
            WHERE type = 'table' AND name IN ('lyrics_cache', 'lyrics_choice', 'lyrics_offset')
            ORDER BY name;
            """;

        using var reader = command.ExecuteReader();
        var tables = new List<string>();
        while (reader.Read())
            tables.Add(reader.GetString(0));

        Assert.Equal(["lyrics_cache", "lyrics_choice", "lyrics_offset"], tables);
        Assert.True(File.Exists(fixture.DatabasePath));
    }

    [Fact]
    public async Task 异步读写应与同步路径等价()
    {
        const string key = "sqlite-test:async";
        var lines = new List<LyricLine> { new(1000, "异步歌词") };
        ILyricsCacheStore cache = fixture.Cache;

        await cache.PutAsync(key, LyricsLoadResult.Loaded, lines);
        var hit = await cache.GetAsync(key);

        Assert.NotNull(hit);
        Assert.Equal(LyricsLoadResult.Loaded, hit.Value.Status);
        Assert.Equal("异步歌词", hit.Value.Lines.Single().Text);
    }

    [Fact]
    public async Task 自动歌词缓存持久保存决策证据且兼容旧数组格式()
    {
        const string key = "sqlite-test:decision";
        var candidate = new LyricCandidate("QQ", "QQ 音乐", "123", "唤归", "王子健/十八薰", "唤归", 226000, "Perfect");
        var decision = new LyricsDecisionEvidence(2, "query-fingerprint", candidate);
        ILyricsCacheStore cache = fixture.Cache;
        await cache.PutDecisionAsync(key, LyricsLoadResult.Loaded, [new(0, "决策歌词")], decision);
        var restarted = new SqliteLyricsCacheStore(fixture.Database);
        var hit = await ((ILyricsCacheStore)restarted).GetAsync(key);
        Assert.Equal(decision, hit!.Value.Decision);
        Assert.Equal("决策歌词", hit.Value.Lines.Single().Text);

        using var connection = fixture.Database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO lyrics_cache(key, status, last_used_utc_ms, payload)
            VALUES('sqlite-test:legacy-array', $status, 0, '[{"StartTime":1000,"Text":"旧候选词"}]');
            """;
        command.Parameters.AddWithValue("$status", (int)LyricsLoadResult.Loaded);
        command.ExecuteNonQuery();
        var legacy = await cache.GetAsync("sqlite-test:legacy-array");
        Assert.NotNull(legacy);
        Assert.Equal("旧候选词", legacy.Value.Lines.Single().Text);
        Assert.Null(legacy.Value.Decision);
    }

    [Fact]
    public void Open_connection_applies_per_connection_pragmas()
    {
        using var connection = fixture.Database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA synchronous; PRAGMA busy_timeout;";
        using var reader = command.ExecuteReader();

        Assert.True(reader.Read());
        Assert.Equal(1, reader.GetInt32(0)); // NORMAL
        Assert.True(reader.NextResult());
        Assert.True(reader.Read());
        Assert.Equal(3000, reader.GetInt32(0));
    }
}

public sealed class SqliteStorageFixture : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "Stanza.Tests", Guid.NewGuid().ToString("N"));

    public string DatabasePath { get; }

    internal LocalDatabase Database { get; }

    public SqliteLyricsCacheStore Cache { get; }

    public SqliteLyricsChoiceStore Choices { get; }

    public SqliteStorageFixture()
    {
        Directory.CreateDirectory(_directory);
        DatabasePath = Path.Combine(_directory, "stanza.db");
        Environment.SetEnvironmentVariable("STANZA_DATABASE_PATH", DatabasePath);
        Environment.SetEnvironmentVariable(
            "STANZA_LEGACY_LYRICS_CACHE_PATH",
            Path.Combine(_directory, "legacy-lyrics-cache.json"));
        Environment.SetEnvironmentVariable(
            "STANZA_LEGACY_CHOICE_PATH",
            Path.Combine(_directory, "legacy-lyrics-choice.json"));

        Database = new LocalDatabase(DatabasePath);
        Database.Initialize();
        Cache = new SqliteLyricsCacheStore(Database);
        Choices = new SqliteLyricsChoiceStore(Database);
        Cache.Initialize();
        Choices.Initialize();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Environment.SetEnvironmentVariable("STANZA_DATABASE_PATH", null);
        Environment.SetEnvironmentVariable("STANZA_LEGACY_LYRICS_CACHE_PATH", null);
        Environment.SetEnvironmentVariable("STANZA_LEGACY_CHOICE_PATH", null);
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch
        {
            // 测试清理失败不影响结果。
        }
    }
}

[Collection("SqliteStorage")]
public sealed class LegacyJsonMigrationTests
{
    [Fact]
    public void 旧版歌词缓存和选择JSON应迁移进SQLite()
    {
        var directory = Path.Combine(
            Path.GetTempPath(), "Stanza.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var databasePath = Path.Combine(directory, "stanza.db");
        var cachePath = Path.Combine(directory, "lyrics-cache.json");
        var choicePath = Path.Combine(directory, "lyrics-choice.json");
        try
        {
            File.WriteAllText(cachePath, """
                {
                  "Version": 1,
                  "Entries": [
                    {
                      "Key": "旧歌\u0001旧歌手",
                      "Status": 0,
                      "LastUsedUnixMs": 123,
                      "Lines": [
                        {
                          "StartTime": 1000,
                          "Text": "旧歌词",
                          "EndTime": 2000,
                          "Translation": null,
                          "Words": [{ "Text": "旧", "StartTime": 1000, "EndTime": 1500 }]
                        }
                      ]
                    }
                  ]
                }
                """);
            File.WriteAllText(choicePath, """
                {
                  "Version": 1,
                  "Entries": [
                    {
                      "Key": "旧歌\u0001旧歌手",
                      "LastUsedUnixMs": 123,
                      "Candidate": {
                        "SourceKey": "QQ",
                        "SourceName": "QQ 音乐",
                        "Id": "123",
                        "Title": "旧歌",
                        "Artist": "旧歌手",
                        "Album": null,
                        "DurationMs": 180000,
                        "MatchType": "Perfect"
                      }
                    }
                  ]
                }
                """);

            Environment.SetEnvironmentVariable("STANZA_DATABASE_PATH", databasePath);
            Environment.SetEnvironmentVariable("STANZA_LEGACY_LYRICS_CACHE_PATH", cachePath);
            Environment.SetEnvironmentVariable("STANZA_LEGACY_CHOICE_PATH", choicePath);
            var database = new LocalDatabase(databasePath);
            database.Initialize();
            var cache = new SqliteLyricsCacheStore(database);
            var choices = new SqliteLyricsChoiceStore(database);
            cache.Initialize();
            choices.Initialize();

            Assert.True(cache.TryGet("旧歌\u0001旧歌手", out var status, out var lines));
            Assert.Equal(LyricsLoadResult.Loaded, status);
            Assert.Equal("旧歌词", lines.Single().Text);
            Assert.True(choices.TryGet("旧歌\u0001旧歌手", out var candidate));
            Assert.Equal("123", candidate!.Id);
            Assert.True(File.Exists(cachePath + ".migrated"));
            Assert.True(File.Exists(choicePath + ".migrated"));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Environment.SetEnvironmentVariable("STANZA_DATABASE_PATH", null);
            Environment.SetEnvironmentVariable("STANZA_LEGACY_LYRICS_CACHE_PATH", null);
            Environment.SetEnvironmentVariable("STANZA_LEGACY_CHOICE_PATH", null);
            try { Directory.Delete(directory, recursive: true); } catch { }
        }
    }
}
