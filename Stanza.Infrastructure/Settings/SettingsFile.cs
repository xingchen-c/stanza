using System.IO;
using System.Text.Json;
using Stanza.Core.Models;

namespace Stanza.Infrastructure.Settings;

/// <summary>将设置值读写为应用数据目录中的 JSON；系统自启另由 WindowsAutoStart 执行。</summary>
public sealed class SettingsFile : ISettingsStore
{
    private static readonly JsonSerializerOptions IndentedJson = new() { WriteIndented = true };

    /// <summary>读取设置；文件缺失或损坏时返回默认值</summary>
    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(AppDataPaths.SettingsPath))
                return new AppSettings();

            var json = File.ReadAllText(AppDataPaths.SettingsPath);
            return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
        }
        catch
        {
            return new AppSettings();
        }
    }

    /// <summary>保存设置</summary>
    public void Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(AppDataPaths.DataDirectory);
            var json = JsonSerializer.Serialize(settings, IndentedJson);
            // 先写临时文件再替换，避免写一半崩溃留下损坏配置（与歌词缓存一致）
            var tmp = AppDataPaths.SettingsPath + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, AppDataPaths.SettingsPath, true);
        }
        catch (Exception ex)
        {
            AppLog.Error("保存设置失败", ex);
        }
    }

}
