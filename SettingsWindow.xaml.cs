using System.Windows;
using System.Windows.Controls;
using FluentLyrics.Models;
using FluentLyrics.Services;

namespace FluentLyrics;

/// <summary>设置窗口：编辑并保存 AppSettings</summary>
public partial class SettingsWindow : Window
{
    // 保存设置时保留与界面无关的字段（如手动位置偏移），避免被默认值覆盖
    private readonly AppSettings _baseSettings;

    public SettingsWindow(AppSettings settings)
    {
        InitializeComponent();
        _baseSettings = settings;

        FontSizeSlider.Value = settings.FontSize;
        OffsetSlider.Value = settings.LyricOffsetMs;
        ClickThroughCheck.IsChecked = settings.ClickThrough;
        MediaControlsCheck.IsChecked = settings.ShowMediaControls;
        HideOnPauseCheck.IsChecked = settings.HideOnPause;
        AutoStartCheck.IsChecked = settings.AutoStart;
        FluentFlyoutCheck.IsChecked = settings.TaskbarLocatorWhitelist
            .Any(k => string.Equals(k, "FluentFlyout", StringComparison.OrdinalIgnoreCase));
        IndependentCheck.IsChecked = settings.TaskbarLocatorWhitelist
            .Any(k => string.Equals(k, "Independent", StringComparison.OrdinalIgnoreCase));
        AutoWidthCheck.IsChecked = settings.AutoCardWidth;
        WidthSlider.Value = settings.CardWidth;
        WidthSlider.IsEnabled = !settings.AutoCardWidth;

        foreach (ComboBoxItem item in DirectionCombo.Items)
            if (item.Tag?.ToString() == settings.PlacementDirection)
            {
                DirectionCombo.SelectedItem = item;
                break;
            }

        foreach (ComboBoxItem item in ColorCombo.Items)
            if (item.Tag?.ToString() == settings.LyricColor)
            {
                ColorCombo.SelectedItem = item;
                break;
            }
    }

    /// <summary>保存成功后触发（主窗口据此重新应用设置）</summary>
    public event Action? Saved;

    private void AutoWidth_Changed(object sender, RoutedEventArgs e)
    {
        WidthSlider.IsEnabled = AutoWidthCheck.IsChecked != true;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        // 白名单至少勾选一项：全不勾会导致定位没有依据，禁止保存
        var whitelist = new List<string>();
        if (FluentFlyoutCheck.IsChecked == true)
            whitelist.Add("FluentFlyout");
        if (IndependentCheck.IsChecked == true)
            whitelist.Add("Independent");

        if (whitelist.Count == 0)
        {
            MessageBox.Show(this, "至少勾选一种任务栏定位方式。", "FluentLyrics",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var settings = new AppSettings
        {
            FontSize = FontSizeSlider.Value,
            LyricColor = (ColorCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "#FFFFFFFF",
            LyricOffsetMs = (int)OffsetSlider.Value,
            ClickThrough = ClickThroughCheck.IsChecked == true,
            ShowMediaControls = MediaControlsCheck.IsChecked == true,
            HideOnPause = HideOnPauseCheck.IsChecked == true,
            AutoStart = AutoStartCheck.IsChecked == true,
            TaskbarLocatorWhitelist = whitelist,
            PlacementDirection = (DirectionCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "Auto",
            ManualOffsetX = _baseSettings.ManualOffsetX,
            ManualOffsetY = _baseSettings.ManualOffsetY,
            AutoCardWidth = AutoWidthCheck.IsChecked == true,
            CardWidth = WidthSlider.Value
        };

        AppSettingsService.Save(settings);
        AppSettingsService.ApplyAutoStart(settings.AutoStart);
        Saved?.Invoke();
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
