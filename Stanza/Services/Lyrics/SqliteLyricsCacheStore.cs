using System.IO;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Stanza.Models;
using Stanza.Services.Storage;

namespace Stanza.Services.Lyrics;

/// <summary>
///     歌词 SQLite 缓存：%APPDATA%\Stanza\stanza.db / lyrics_cache。
///     缓存 Loaded / Instrumental 结果及逐字、翻译时间轴；首次运行会迁移旧 lyrics-cache.json。
/// </summary>
public sealed class SqliteLyricsCacheStore : ILyricsCacheStore
{
    private const int MaxEntries = 300;
    private const int CacheVersion = 1;

    private readonly LocalDatabase _database;
    private readonly object _gate = new();
    private bool _legacyMigrated;

    internal SqliteLyricsCacheStore(LocalDatabase database)
    {
        _database = database;
    }

    private static string LegacyCachePath =>
        Environment.GetEnvironmentVariable("STANZA_LEGACY_LYRICS_CACHE_PATH") is { Length: > 0 } overridePath
            ? overridePath
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Stanza", "lyrics-cache.json");

    /// <summary>应用启动时初始化 SQLite 并迁移旧 JSON 缓存。</summary>
    public void Initialize()
    {
        lock (_gate)
            EnsureLegacyMigrated();
    }

    /// <summary>按 key 取缓存；数据库无记录或解析失败时返回 false。</summary>
    public bool TryGet(string key, out LyricsLoadResult status, out List<LyricLine> lines)
    {
        status = LyricsLoadResult.NotFound;
        lines = [];

        lock (_gate)
        {
            try
            {
                EnsureLegacyMigrated();
                using var connection = _database.Open();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    SELECT status, payload
                    FROM lyrics_cache
                    WHERE key = $key;
                    """;
                command.Parameters.AddWithValue("$key", key);

                using var reader = command.ExecuteReader();
                if (!reader.Read())
                    return false;

                status = (LyricsLoadResult)reader.GetInt32(0);
                var payload = reader.GetString(1);
                var dto = JsonSerializer.Deserialize<List<CachedLyricLine>>(payload);
                if (dto is null || dto.Count == 0)
                    return false;

                reader.Close();
                Touch(connection, key);
                lines = dto.Select(FromDto).ToList();
                return true;
            }
            catch (Exception ex)
            {
                AppLog.Warn($"歌词缓存读取失败（降级为未命中）: {ex.Message}");
                status = LyricsLoadResult.NotFound;
                lines = [];
                return false;
            }
        }
    }

    /// <summary>写入或覆盖缓存，并将总记录数控制在 MaxEntries。</summary>
    public void Put(string key, LyricsLoadResult status, IReadOnlyList<LyricLine> lines)
    {
        if (lines.Count == 0)
            return;

        lock (_gate)
        {
            try
            {
                EnsureLegacyMigrated();
                var payload = JsonSerializer.Serialize(lines.Select(ToDto).ToList());
                var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

                using var connection = _database.Open();
                using var transaction = connection.BeginTransaction();
                using (var command = connection.CreateCommand())
                {
                    command.Transaction = transaction;
                    command.CommandText = """
                        INSERT INTO lyrics_cache(key, status, last_used_utc_ms, payload)
                        VALUES($key, $status, $lastUsed, $payload)
                        ON CONFLICT(key) DO UPDATE SET
                            status = excluded.status,
                            last_used_utc_ms = excluded.last_used_utc_ms,
                            payload = excluded.payload;
                        """;
                    command.Parameters.AddWithValue("$key", key);
                    command.Parameters.AddWithValue("$status", (int)status);
                    command.Parameters.AddWithValue("$lastUsed", now);
                    command.Parameters.AddWithValue("$payload", payload);
                    command.ExecuteNonQuery();
                }

                Prune(connection, transaction, MaxEntries);
                transaction.Commit();
            }
            catch (Exception ex)
            {
                AppLog.Warn($"歌词缓存写入失败（降级为不缓存）: {ex.Message}");
            }
        }
    }

    /// <summary>清空 SQLite 歌词缓存。</summary>
    public void Clear()
    {
        lock (_gate)
        {
            try
            {
                using var connection = _database.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "DELETE FROM lyrics_cache;";
                command.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                AppLog.Warn($"歌词缓存清空失败: {ex.Message}");
            }
        }
    }

    private static void Touch(SqliteConnection connection, string key)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE lyrics_cache
            SET last_used_utc_ms = $lastUsed
            WHERE key = $key;
            """;
        command.Parameters.AddWithValue("$lastUsed", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$key", key);
        command.ExecuteNonQuery();
    }

    private static void Prune(
        SqliteConnection connection,
        SqliteTransaction transaction,
        int maxEntries)
    {
        using (var countCommand = connection.CreateCommand())
        {
            countCommand.Transaction = transaction;
            countCommand.CommandText = "SELECT COUNT(*) FROM lyrics_cache;";
            var count = Convert.ToInt32(countCommand.ExecuteScalar());
            var overflow = count - maxEntries;
            if (overflow <= 0)
                return;

            using var deleteCommand = connection.CreateCommand();
            deleteCommand.Transaction = transaction;
            deleteCommand.CommandText = """
                DELETE FROM lyrics_cache
                WHERE key IN (
                    SELECT key FROM lyrics_cache
                    ORDER BY last_used_utc_ms ASC
                    LIMIT $overflow
                );
                """;
            deleteCommand.Parameters.AddWithValue("$overflow", overflow);
            deleteCommand.ExecuteNonQuery();
        }
    }

    private void EnsureLegacyMigrated()
    {
        if (_legacyMigrated)
            return;

        _legacyMigrated = true;
        if (!File.Exists(LegacyCachePath))
            return;

        try
        {
            using var connection = _database.Open();
            using (var countCommand = connection.CreateCommand())
            {
                countCommand.CommandText = "SELECT COUNT(*) FROM lyrics_cache;";
                if (Convert.ToInt32(countCommand.ExecuteScalar()) > 0)
                {
                    File.Move(LegacyCachePath, LegacyCachePath + ".migrated", true);
                    return;
                }
            }

            var json = File.ReadAllText(LegacyCachePath);
            var file = JsonSerializer.Deserialize<LyricsCacheFile>(json);
            if (file is not { Version: CacheVersion, Entries: not null })
                return;

            using var transaction = connection.BeginTransaction();
            foreach (var entry in file.Entries)
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT OR REPLACE INTO lyrics_cache(key, status, last_used_utc_ms, payload)
                    VALUES($key, $status, $lastUsed, $payload);
                    """;
                command.Parameters.AddWithValue("$key", entry.Key);
                command.Parameters.AddWithValue("$status", (int)entry.Status);
                command.Parameters.AddWithValue("$lastUsed", entry.LastUsedUnixMs);
                command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(entry.Lines));
                command.ExecuteNonQuery();
            }

            transaction.Commit();
            File.Move(LegacyCachePath, LegacyCachePath + ".migrated", true);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"旧歌词 JSON 迁移失败，保留原文件: {ex.Message}");
        }
    }

    private static CachedLyricLine ToDto(LyricLine line)
    {
        return new CachedLyricLine
        {
            StartTime = line.StartTime,
            Text = line.Text,
            EndTime = line.EndTime,
            Translation = line.Translation,
            Words = line.Words?.Select(w => new CachedLyricWord
            {
                Text = w.Text,
                StartTime = w.StartTime,
                EndTime = w.EndTime
            }).ToList()
        };
    }

    private static LyricLine FromDto(CachedLyricLine dto)
    {
        return new LyricLine(
            dto.StartTime,
            dto.Text,
            dto.EndTime,
            dto.Translation,
            dto.Words?.Select(w => new LyricWord(w.Text, w.StartTime, w.EndTime)).ToList());
    }

    private sealed class LyricsCacheFile
    {
        public int Version { get; set; } = 1;
        public List<CachedLyricLineEntry> Entries { get; set; } = [];
    }

    private sealed class CachedLyricLineEntry
    {
        public string Key { get; set; } = string.Empty;
        public LyricsLoadResult Status { get; set; }
        public long LastUsedUnixMs { get; set; }
        public List<CachedLyricLine> Lines { get; set; } = [];
    }

    private sealed class CachedLyricLine
    {
        public int StartTime { get; set; }
        public string Text { get; set; } = string.Empty;
        public int? EndTime { get; set; }
        public string? Translation { get; set; }
        public List<CachedLyricWord>? Words { get; set; }
    }

    private sealed class CachedLyricWord
    {
        public string Text { get; set; } = string.Empty;
        public int StartTime { get; set; }
        public int EndTime { get; set; }
    }
}
