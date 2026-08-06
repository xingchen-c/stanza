using System.IO;
using System.Text.Json;
using FluentLyrics.Models;

namespace FluentLyrics.Services.Lyrics;

/// <summary>
///     歌词磁盘缓存：%APPDATA%\FluentLyrics\lyrics-cache.json。
///     重启后听过的歌秒出、QQ 接口挂起时限流时兜底；逐字时间戳一并缓存。
/// </summary>
public static class LyricsCacheService
{
    private const int MaxEntries = 300;
    private const int CacheVersion = 1;

    private static readonly string CachePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "FluentLyrics", "lyrics-cache.json");

    private static readonly object Gate = new();
    private static Dictionary<string, LyricsCacheEntry>? _entries; // 惰性加载

    /// <summary>按 key 取缓存（内存命中/磁盘读取）；无缓存返回 false</summary>
    public static bool TryGet(string key, out LyricsLoadResult status, out List<LyricLine> lines)
    {
        lock (Gate)
        {
            var entries = EnsureLoaded();
            if (!entries.TryGetValue(key, out var entry) || entry.Lines.Count == 0)
            {
                status = LyricsLoadResult.NotFound;
                lines = [];
                return false;
            }

            entry.LastUsedUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            status = entry.Status;
            lines = entry.Lines.Select(FromDto).ToList();
            return true;
        }
    }

    /// <summary>写入缓存（Loaded/Instrumental 才调用；NotFound 不入缓存）</summary>
    public static void Put(string key, LyricsLoadResult status, IReadOnlyList<LyricLine> lines)
    {
        lock (Gate)
        {
            var entries = EnsureLoaded();
            entries[key] = new LyricsCacheEntry
            {
                Key = key,
                Status = status,
                LastUsedUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Lines = lines.Select(ToDto).ToList()
            };

            // 简单容量控制：超限时丢掉最久未用的
            if (entries.Count > MaxEntries)
                foreach (var stale in entries.Values
                             .OrderBy(e => e.LastUsedUnixMs)
                             .Take(entries.Count - MaxEntries))
                    entries.Remove(stale.Key);

            Save();
        }
    }

    /// <summary>清空缓存（内存 + 磁盘）</summary>
    public static void Clear()
    {
        lock (Gate)
        {
            _entries = new Dictionary<string, LyricsCacheEntry>();
            Save();
        }
    }

    private static Dictionary<string, LyricsCacheEntry> EnsureLoaded()
    {
        if (_entries is not null)
            return _entries;

        try
        {
            if (File.Exists(CachePath))
            {
                var json = File.ReadAllText(CachePath);
                var file = JsonSerializer.Deserialize<LyricsCacheFile>(json);
                if (file is { Version: CacheVersion, Entries: not null }) _entries = file.Entries.ToDictionary(e => e.Key, StringComparer.Ordinal);
            }
        }
        catch
        {
            // 缓存文件损坏/读失败：按空缓存处理，不影响主流程
        }

        _entries ??= new Dictionary<string, LyricsCacheEntry>();
        return _entries;
    }

    private static void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(CachePath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            var file = new LyricsCacheFile
            {
                Version = CacheVersion,
                Entries = _entries?.Values.ToList() ?? []
            };
            var json = JsonSerializer.Serialize(file, new JsonSerializerOptions { WriteIndented = true });

            // 先写临时文件再替换，避免写一半崩溃留下损坏缓存
            var tmp = CachePath + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, CachePath, true);
        }
        catch
        {
            // 磁盘写失败不影响主流程（最多就是下次不命中）
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
}

/// <summary>磁盘缓存文件（带版本号，便于以后改结构）</summary>
internal sealed class LyricsCacheFile
{
    public int Version { get; set; } = 1;
    public List<LyricsCacheEntry> Entries { get; set; } = [];
}

/// <summary>单首歌曲的缓存条目</summary>
internal sealed class LyricsCacheEntry
{
    public string Key { get; set; } = string.Empty;
    public LyricsLoadResult Status { get; set; }
    public long LastUsedUnixMs { get; set; }
    public List<CachedLyricLine> Lines { get; set; } = [];
}

internal sealed class CachedLyricLine
{
    public int StartTime { get; set; }
    public string Text { get; set; } = string.Empty;
    public int? EndTime { get; set; }
    public string? Translation { get; set; }
    public List<CachedLyricWord>? Words { get; set; }
}

internal sealed class CachedLyricWord
{
    public string Text { get; set; } = string.Empty;
    public int StartTime { get; set; }
    public int EndTime { get; set; }
}