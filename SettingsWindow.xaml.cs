using System.Windows;
using System.Windows.Controls;
using FluentLyrics.Models;
using FluentLyrics.Services;

namespace FluentLyrics;

/// <summary>设置窗口：编辑并保存 AppSettings</summary>
public partial class SettingsWindow : Window
{
    /// <summary>保存成功后触发（主窗口据此重新应用设置）</summary>
    public event Action? Saved;

    public SettingsWindow(AppSettings settings)
    {
        InitializeComponent();

        FontSizeSlider.Value = settings.FontSize;
        SpeedSlider.Value = settings.MarqueeSpeed;
        OffsetSlider.Value = settings.LyricOffsetMs;
        ClickThroughCheck.IsChecked = settings.ClickThrough;
        AutoStartCheck.IsChecked = settings.AutoStart;

        foreach (ComboBoxItem item in ColorCombo.Items)
        {
            if (item.Tag?.ToString() == settings.LyricColor)
            {
                ColorCombo.SelectedItem = item;
                break;
            }
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var settings = new AppSettings
        {
            FontSize = FontSizeSlider.Value,
            LyricColor = (ColorCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "#FFFFFFFF",
            MarqueeSpeed = SpeedSlider.Value,
            LyricOffsetMs = (int)OffsetSlider.Value,
            ClickThrough = ClickThroughCheck.IsChecked == true,
            AutoStart = AutoStartCheck.IsChecked == true,
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
