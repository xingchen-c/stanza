using Stanza.Core.Models;

namespace Stanza;

/// <summary>设置窗口的编辑状态、校验和 AppSettings 构造逻辑。</summary>
internal sealed class SettingsViewModel
{
    public double FontSize { get; set; }
    public string LyricColor { get; set; } = "#FFFFFFFF";
    public int LyricOffsetMs { get; set; }
    public bool ClickThrough { get; set; }
    public bool ShowMediaControls { get; set; }
    public bool HideOnPause { get; set; }
    public bool AutoStart { get; set; }
    public List<string> TaskbarLocatorWhitelist { get; set; } = [];
    public string PlacementDirection { get; set; } = "Auto";
    public bool AutoCardWidth { get; set; }
    public double CardWidth { get; set; }
    public string CompatibilityMode { get; set; } = "Auto";
    public string AnimationSpeed { get; set; } = "Normal";

    public bool IsValid => TaskbarLocatorWhitelist.Count > 0;

    public static SettingsViewModel FromSettings(AppSettings settings)
    {
        return new SettingsViewModel
        {
            FontSize = settings.FontSize,
            LyricColor = settings.LyricColor,
            LyricOffsetMs = settings.LyricOffsetMs,
            ClickThrough = settings.ClickThrough,
            ShowMediaControls = settings.ShowMediaControls,
            HideOnPause = settings.HideOnPause,
            AutoStart = settings.AutoStart,
            TaskbarLocatorWhitelist = [.. settings.TaskbarLocatorWhitelist],
            PlacementDirection = settings.PlacementDirection,
            AutoCardWidth = settings.AutoCardWidth,
            CardWidth = settings.CardWidth,
            CompatibilityMode = settings.CompatibilityMode,
            AnimationSpeed = settings.AnimationSpeed
        };
    }

    public AppSettings ToSettings(AppSettings baseSettings)
    {
        return new AppSettings
        {
            FontSize = FontSize,
            LyricColor = LyricColor,
            LyricOffsetMs = LyricOffsetMs,
            ClickThrough = ClickThrough,
            ShowMediaControls = ShowMediaControls,
            HideOnPause = HideOnPause,
            AutoStart = AutoStart,
            TaskbarLocatorWhitelist = [.. TaskbarLocatorWhitelist],
            PlacementDirection = PlacementDirection,
            ManualOffsetX = baseSettings.ManualOffsetX,
            ManualOffsetY = baseSettings.ManualOffsetY,
            // 设置界面没有日志级别控件；该字段由高级用户手改 settings.json。
            // 必须透传，否则点一次保存就会把 Debug 打回默认 Info。
            LogLevel = baseSettings.LogLevel,
            AutoCardWidth = AutoCardWidth,
            CardWidth = CardWidth,
            CompatibilityMode = CompatibilityMode,
            AnimationSpeed = AnimationSpeed
        };
    }
}
