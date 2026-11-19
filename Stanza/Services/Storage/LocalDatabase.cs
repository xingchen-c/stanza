using System.IO;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Data.Sqlite;

namespace Stanza.Services.Storage;

/// <summary>
///     本地 SQLite 数据库。负责创建目录、建立表结构和提供连接；
///     歌词缓存与用户选择记忆共用同一个 stanza.db。
/// </summary>
internal sealed class LocalDatabase
{
    private readonly object _gate = new();
    private bool _initialized;

    public LocalDatabase(string? databasePath = null)
    {
        DatabasePath = databasePath ??
            (Environment.GetEnvironmentVariable("STANZA_DATABASE_PATH") is { Length: > 0 } overridePath
                ? overridePath
                : AppDataPaths.DatabasePath);
    }

    /// <summary>当前数据库文件路径。</summary>
    public string DatabasePath { get; }

    /// <summary>主动创建目录、数据库和表结构；数据库已初始化时直接返回。</summary>
    public void Initialize() => EnsureInitialized();

    /// <summary>打开已初始化的 SQLite 连接；调用方负责释放。</summary>
    public SqliteConnection Open()
    {
        EnsureInitialized();
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate
        }.ToString());
        connection.Open();

        // synchronous / busy_timeout 是 per-connection 设置；journal_mode=WAL 已持久化在文件中。
        Execute(connection, "PRAGMA synchronous=NORMAL;");
        Execute(connection, "PRAGMA busy_timeout=3000;");
        return connection;
    }

    private void EnsureInitialized()
    {
        if (_initialized)
            return;

        lock (_gate)
        {
            if (_initialized)
                return;

            var dir = Path.GetDirectoryName(DatabasePath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = DatabasePath,
                Mode = SqliteOpenMode.ReadWriteCreate
            }.ToString());
            connection.Open();

            Execute(connection, "PRAGMA journal_mode=WAL;");
            Execute(connection, "PRAGMA synchronous=NORMAL;");
            Execute(connection, "PRAGMA busy_timeout=3000;");
            Execute(connection, """
                CREATE TABLE IF NOT EXISTS lyrics_cache (
                    key TEXT PRIMARY KEY,
                    status INTEGER NOT NULL,
                    last_used_utc_ms INTEGER NOT NULL,
                    payload TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS idx_lyrics_cache_last_used
                    ON lyrics_cache(last_used_utc_ms);

                CREATE TABLE IF NOT EXISTS lyrics_choice (
                    key TEXT PRIMARY KEY,
                    last_used_utc_ms INTEGER NOT NULL,
                    payload TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS idx_lyrics_choice_last_used
                    ON lyrics_choice(last_used_utc_ms);
                """);

            _initialized = true;
        }
    }

    [SuppressMessage("Security", "CA2100", Justification = "SQL 仅由内部固定 schema 常量组成，不接收用户输入。")]
    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
