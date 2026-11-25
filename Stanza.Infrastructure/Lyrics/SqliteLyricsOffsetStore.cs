using Stanza.Infrastructure.Storage;

namespace Stanza.Infrastructure.Lyrics;

/// <summary>单曲偏移属于用户配置，独立于可淘汰、可清除的歌词缓存和选择记忆。</summary>
internal sealed class SqliteLyricsOffsetStore(LocalDatabase database) : ILyricsOffsetStore
{
    private readonly Dictionary<string, int> _items = [];

    public void Initialize()
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT key, offset_ms FROM lyrics_offset;";
        using var reader = command.ExecuteReader();
        _items.Clear();
        while (reader.Read())
            _items[reader.GetString(0)] = reader.GetInt32(1);
    }

    // 启动时读入，渲染 ticker 只访问内存；写入由 UI 线程串行调用。
    public int Get(string key) => _items.GetValueOrDefault(key);

    public bool Set(string key, int offsetMs)
    {
        try
        {
            using var connection = database.Open();
            using var command = connection.CreateCommand();
            command.CommandText = offsetMs == 0
                ? "DELETE FROM lyrics_offset WHERE key = $key;"
                : """
                  INSERT INTO lyrics_offset(key, offset_ms) VALUES($key, $offset)
                  ON CONFLICT(key) DO UPDATE SET offset_ms = excluded.offset_ms;
                  """;
            command.Parameters.AddWithValue("$key", key);
            if (offsetMs != 0)
                command.Parameters.AddWithValue("$offset", offsetMs);
            command.ExecuteNonQuery();
            if (offsetMs == 0)
                _items.Remove(key);
            else
                _items[key] = offsetMs;
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"单曲歌词偏移保存失败: {ex.Message}");
            return false;
        }
    }
}
