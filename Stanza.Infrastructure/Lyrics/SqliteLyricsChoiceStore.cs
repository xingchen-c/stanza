using System.IO;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Stanza.Core.Models;
using Stanza.Infrastructure.Storage;

namespace Stanza.Infrastructure.Lyrics;

/// <summary>
///     用户手动选择歌词的记忆：应用数据目录 stanza.db / lyrics_choice。
///     key（歌名 + 歌手）→ 用户选中的候选；首次运行会迁移旧 lyrics-choice.json。
/// </summary>
public sealed class SqliteLyricsChoiceStore : ILyricsChoiceStore
{
    private const int MaxEntries = 200;
    private const int CacheVersion = 1;

    private readonly LocalDatabase _database;
    private readonly object _gate = new();
    private bool _legacyMigrated;

    internal SqliteLyricsChoiceStore(LocalDatabase database)
    {
        _database = database;
    }

    private static string LegacyChoicePath =>
        Environment.GetEnvironmentVariable("STANZA_LEGACY_CHOICE_PATH") is { Length: > 0 } overridePath
            ? overridePath
            : Path.Combine(AppDataPaths.DataDirectory, "lyrics-choice.json");

    /// <summary>应用启动时初始化 SQLite 并迁移旧 JSON 选择记忆。</summary>
    public void Initialize()
    {
        lock (_gate)
            EnsureLegacyMigrated();
    }

    /// <summary>取某首歌的记忆选择；无记录或解析失败时返回 false。</summary>
    public bool TryGet(string key, out LyricCandidate? candidate)
    {
        candidate = null!;
        lock (_gate)
        {
            try
            {
                EnsureLegacyMigrated();
                using var connection = _database.Open();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    SELECT payload
                    FROM lyrics_choice
                    WHERE key = $key;
                    """;
                command.Parameters.AddWithValue("$key", key);

                var payload = command.ExecuteScalar() as string;
                if (string.IsNullOrWhiteSpace(payload))
                    return false;

                var parsed = JsonSerializer.Deserialize<LyricCandidate>(payload);
                if (parsed is null)
                    return false;

                Touch(connection, key);
                candidate = parsed;
                return true;
            }
            catch (Exception ex)
            {
                AppLog.Warn($"歌词选择记忆读取失败（降级为未命中）: {ex.Message}");
                candidate = null!;
                return false;
            }
        }
    }

    /// <summary>记录某首歌的选择（覆盖旧选择），并控制总记录数。</summary>
    public void Put(string key, LyricCandidate candidate)
    {
        lock (_gate)
        {
            try
            {
                EnsureLegacyMigrated();
                using var connection = _database.Open();
                using var transaction = connection.BeginTransaction();
                using (var command = connection.CreateCommand())
                {
                    command.Transaction = transaction;
                    command.CommandText = """
                        INSERT INTO lyrics_choice(key, last_used_utc_ms, payload)
                        VALUES($key, $lastUsed, $payload)
                        ON CONFLICT(key) DO UPDATE SET
                            last_used_utc_ms = excluded.last_used_utc_ms,
                            payload = excluded.payload;
                        """;
                    command.Parameters.AddWithValue("$key", key);
                    command.Parameters.AddWithValue(
                        "$lastUsed",
                        DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                    command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(candidate));
                    command.ExecuteNonQuery();
                }

                Prune(connection, transaction);
                transaction.Commit();
            }
            catch (Exception ex)
            {
                AppLog.Warn($"歌词选择记忆写入失败（降级为不记忆）: {ex.Message}");
            }
        }
    }

    public void Remove(string key)
    {
        lock (_gate)
        {
            EnsureLegacyMigrated();
            using var connection = _database.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM lyrics_choice WHERE key = $key;";
            command.Parameters.AddWithValue("$key", key);
            command.ExecuteNonQuery();
        }
    }

    /// <summary>清空用户选择记忆。</summary>
    public void Clear()
    {
        lock (_gate)
        {
            try
            {
                using var connection = _database.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "DELETE FROM lyrics_choice;";
                command.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                AppLog.Warn($"歌词选择记忆清空失败: {ex.Message}");
            }
        }
    }

    private static void Touch(SqliteConnection connection, string key)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE lyrics_choice
            SET last_used_utc_ms = $lastUsed
            WHERE key = $key;
            """;
        command.Parameters.AddWithValue("$lastUsed", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$key", key);
        command.ExecuteNonQuery();
    }

    private static void Prune(SqliteConnection connection, SqliteTransaction transaction)
    {
        using (var countCommand = connection.CreateCommand())
        {
            countCommand.Transaction = transaction;
            countCommand.CommandText = "SELECT COUNT(*) FROM lyrics_choice;";
            var count = Convert.ToInt32(countCommand.ExecuteScalar());
            var overflow = count - MaxEntries;
            if (overflow <= 0)
                return;

            using var deleteCommand = connection.CreateCommand();
            deleteCommand.Transaction = transaction;
            deleteCommand.CommandText = """
                DELETE FROM lyrics_choice
                WHERE key IN (
                    SELECT key FROM lyrics_choice
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
        if (!File.Exists(LegacyChoicePath))
            return;

        try
        {
            using var connection = _database.Open();
            using (var countCommand = connection.CreateCommand())
            {
                countCommand.CommandText = "SELECT COUNT(*) FROM lyrics_choice;";
                if (Convert.ToInt32(countCommand.ExecuteScalar()) > 0)
                {
                    File.Move(LegacyChoicePath, LegacyChoicePath + ".migrated", true);
                    return;
                }
            }

            var json = File.ReadAllText(LegacyChoicePath);
            var file = JsonSerializer.Deserialize<ChoiceFile>(json);
            if (file is not { Version: CacheVersion, Entries: not null })
                return;

            using var transaction = connection.BeginTransaction();
            foreach (var entry in file.Entries)
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT OR REPLACE INTO lyrics_choice(key, last_used_utc_ms, payload)
                    VALUES($key, $lastUsed, $payload);
                    """;
                command.Parameters.AddWithValue("$key", entry.Key);
                command.Parameters.AddWithValue("$lastUsed", entry.LastUsedUnixMs);
                command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(entry.Candidate));
                command.ExecuteNonQuery();
            }

            transaction.Commit();
            File.Move(LegacyChoicePath, LegacyChoicePath + ".migrated", true);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"旧歌词选择 JSON 迁移失败，保留原文件: {ex.Message}");
        }
    }

    private sealed class ChoiceFile
    {
        public int Version { get; set; }
        public List<ChoiceEntry> Entries { get; set; } = [];
    }

    private sealed class ChoiceEntry
    {
        public string Key { get; set; } = string.Empty;
        public LyricCandidate Candidate { get; set; } = null!;
        public long LastUsedUnixMs { get; set; }
    }
}
