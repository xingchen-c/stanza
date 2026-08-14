using System.IO;
using System.Text.Json;
using Stanza.Models;

namespace Stanza.Services.Lyrics;

/// <summary>
///     用户手动选择歌词的记忆：%APPDATA%\Stanza\lyrics-choice.json。
///     key（歌名 + 歌手）→ 用户选中的候选；以后听到同一首歌时优先用该候选取词。
/// </summary>
public static class LyricsChoiceService
{
    private const int MaxEntries = 200;
    private const int CacheVersion = 1;

    private static readonly string ChoicePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Stanza", "lyrics-choice.json");

    private static readonly object Gate = new();
    private static Dictionary<string, ChoiceEntry>? _entries; // 惰性加载

    /// <summary>取某首歌的记忆选择；无则返回 false</summary>
    public static bool TryGet(string key, out LyricCandidate candidate)
    {
        lock (Gate)
        {
            var entries = EnsureLoaded();
            if (entries.TryGetValue(key, out var entry))
            {
                entry.LastUsedUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                candidate = entry.Candidate;
                return true;
            }

            candidate = null!;
            return false;
        }
    }

    /// <summary>记录某首歌的选择（覆盖旧选择）</summary>
    public static void Put(string key, LyricCandidate candidate)
    {
        lock (Gate)
        {
            var entries = EnsureLoaded();
            entries[key] = new ChoiceEntry
            {
                Key = key,
                Candidate = candidate,
                LastUsedUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            };

            // 简单容量控制：超限丢最久未用的
            if (entries.Count > MaxEntries)
                foreach (var stale in entries.Values
                             .OrderBy(e => e.LastUsedUnixMs)
                             .Take(entries.Count - MaxEntries))
                    entries.Remove(stale.Key);

            Save();
        }
    }

    /// <summary>清空选择记忆（跟"清除歌词缓存"一起用）</summary>
    public static void Clear()
    {
        lock (Gate)
        {
            _entries = new Dictionary<string, ChoiceEntry>();
            Save();
        }
    }

    private static Dictionary<string, ChoiceEntry> EnsureLoaded()
    {
        if (_entries is not null)
            return _entries;

        _entries = new Dictionary<string, ChoiceEntry>();
        try
        {
            if (!File.Exists(ChoicePath))
                return _entries;

            var json = File.ReadAllText(ChoicePath);
            var file = JsonSerializer.Deserialize<ChoiceFile>(json);
            if (file is { Version: CacheVersion, Entries: not null })
                _entries = file.Entries.ToDictionary(e => e.Key, StringComparer.Ordinal);
        }
        catch
        {
            // 损坏的配置文件按空处理，不阻塞主流程
        }

        return _entries;
    }

    private static void Save()
    {
        try
        {
            var file = new ChoiceFile
            {
                Version = CacheVersion,
                Entries = _entries?.Values.ToList() ?? []
            };
            var json = JsonSerializer.Serialize(file, new JsonSerializerOptions { WriteIndented = true });

            // 先写临时文件再替换，避免写一半崩溃留下损坏文件
            var tmp = ChoicePath + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, ChoicePath, true);
        }
        catch
        {
            // 记忆写入失败不影响主流程
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
