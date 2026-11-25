using System.IO;
using System.Text.Json;
using Stanza.Core.Models;

using Stanza.Infrastructure.Storage;
using Xunit;

namespace Stanza.Tests;

[Collection("SqliteStorage")]
public sealed class AppDataPathsTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void 未指定目录时保留普通安装的默认位置(string? directory)
    {
        Assert.Equal(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Stanza"),
            AppDataPaths.ResolveDataDirectory(directory));
    }

    [Fact]
    public void 外部绝对目录支持空格和环境变量()
    {
        var path = Path.Combine(Path.GetTempPath(), "Stanza 数据");
        Assert.Equal(path, AppDataPaths.ResolveDataDirectory(path));
        Assert.Equal(
            Path.GetFullPath(Path.Combine(Environment.GetEnvironmentVariable("TEMP")!, "Stanza 数据")),
            AppDataPaths.ResolveDataDirectory("%TEMP%\\Stanza 数据"));
    }

    [Fact]
    public void 相对目录按程序所在目录解析()
    {
        Assert.Equal(
            Path.Combine(AppContext.BaseDirectory, "data"),
            AppDataPaths.ResolveDataDirectory("data"));
    }

    [Fact]
    public void 设置数据库和日志应共用外部数据目录()
    {
        Assert.True(AppDataPaths.IsCustomDirectory);
        Assert.Equal(TestEnvironment.DataDirectory, AppDataPaths.DataDirectory);

        var settings = new AppSettings { FontSize = 23, LyricOffsetMs = 450 };
        new SettingsFile().Save(settings);
        Assert.True(File.Exists(Path.Combine(TestEnvironment.DataDirectory, "settings.json")));
        Assert.False(File.Exists(AppDataPaths.SettingsPath + ".tmp"));
        Assert.Equal(settings.FontSize, new SettingsFile().Load().FontSize);
        Assert.Equal(settings.LyricOffsetMs, new SettingsFile().Load().LyricOffsetMs);

        var database = new LocalDatabase();
        database.Initialize();
        Assert.Equal(Path.Combine(TestEnvironment.DataDirectory, "stanza.db"), database.DatabasePath);
        Assert.True(File.Exists(database.DatabasePath));

        Assert.Equal(Path.Combine(TestEnvironment.DataDirectory, "logs"), AppLog.LogDirectory);
        Assert.True(Directory.Exists(AppLog.LogDirectory));
    }

    [Fact]
    public void 旧歌词JSON应从外部数据目录迁移()
    {
        const string key = "custom-data:legacy";
        Directory.CreateDirectory(TestEnvironment.DataDirectory);
        var cachePath = Path.Combine(TestEnvironment.DataDirectory, "lyrics-cache.json");
        var choicePath = Path.Combine(TestEnvironment.DataDirectory, "lyrics-choice.json");
        var candidate = new LyricCandidate("QQ", "QQ 音乐", "custom-123", "测试歌曲", "歌手", null, null, "Perfect");
        File.WriteAllText(cachePath, JsonSerializer.Serialize(new
        {
            Version = 1,
            Entries = new[]
            {
                new { Key = key, Status = 0, LastUsedUnixMs = 123, Lines = new[] { new LyricLine(1000, "外部目录歌词") } }
            }
        }));
        File.WriteAllText(choicePath, JsonSerializer.Serialize(new
        {
            Version = 1,
            Entries = new[] { new { Key = key, LastUsedUnixMs = 123, Candidate = candidate } }
        }));

        var services = new StorageBackend();

        Assert.True(services.LyricsCache.TryGet(key, out var status, out var lines));
        Assert.Equal(LyricsLoadResult.Loaded, status);
        Assert.Equal("外部目录歌词", lines.Single().Text);
        Assert.True(services.LyricsChoices.TryGet(key, out var choice));
        Assert.Equal(candidate.Id, choice!.Id);
        Assert.True(File.Exists(cachePath + ".migrated"));
        Assert.True(File.Exists(choicePath + ".migrated"));
    }
}
