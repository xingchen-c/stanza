using System.IO;
using System.Text.Json;
using FluentLyrics.Models;
using Microsoft.Win32;

namespace FluentLyrics.Services;

/// <summary>设置读写：JSON 持久化到 %APPDATA%\FluentLyrics\settings.json，附带开机自启开关</summary>
public static class AppSettingsService
{
    private static readonly string SettingsDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FluentLyrics");

    private static readonly string SettingsPath = Path.Combine(SettingsDir, "settings.json");

    /// <summary>读取设置；文件缺失或损坏时返回默认值</summary>
    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath))
                return new AppSettings();

            var json = File.ReadAllText(SettingsPath);
            return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
        }
        catch
        {
            return new AppSettings();
        }
    }

    /// <summary>保存设置</summary>
    public static void Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(SettingsDir);
            var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
            // 先写临时文件再替换，避免写一半崩溃留下损坏配置（与歌词缓存一致）
            var tmp = SettingsPath + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, SettingsPath, true);
        }
        catch (Exception ex)
        {
            AppLog.Error("保存设置失败", ex);
        }
    }

    /// <summary>应用开机自启（注册表 HKCU Run 键）</summary>
    public static void ApplyAutoStart(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Run", true);
            if (key is null)
                return;

            if (enabled)
                key.SetValue("FluentLyrics", $"\"{Environment.ProcessPath}\"");
            else
                key.DeleteValue("FluentLyrics", false);
        }
        catch
        {
            // 注册表操作失败不影响主流程
        }
    }
}
