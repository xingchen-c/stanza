using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using FluentLyrics.Models;
using FluentLyrics.Services;

namespace FluentLyrics;

/// <summary>设置窗口：编辑并保存 AppSettings</summary>
public partial class SettingsWindow
{
    // 保存设置时保留与界面无关的字段（如手动位置偏移），避免被默认值覆盖
    private readonly AppSettings _baseSettings;

    public SettingsWindow(AppSettings settings)
    {
        InitializeComponent();
        SourceInitialized += (_, _) => TryEnableMica();
        _baseSettings = settings;

        // ToggleSwitch 旋钮动画由代码驱动（XAML 模板触发器无法解析模板内名称）
        WireToggleAnimation(FluentFlyoutCheck);
        WireToggleAnimation(IndependentCheck);
        WireToggleAnimation(AutoWidthCheck);
        WireToggleAnimation(MediaControlsCheck);
        WireToggleAnimation(HideOnPauseCheck);
        WireToggleAnimation(ClickThroughCheck);
        WireToggleAnimation(AutoStartCheck);

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

        // 滑块实时数值显示
        FontSizeSlider.ValueChanged += (_, _) => FontSizeValue.Text = $"{FontSizeSlider.Value:F0} px";
        OffsetSlider.ValueChanged += (_, _) => OffsetValue.Text = $"{OffsetSlider.Value:F0} ms";
        WidthSlider.ValueChanged += (_, _) => WidthValue.Text = $"{WidthSlider.Value:F0} px";

        // 页头版本号
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        if (version is not null)
            VersionText.Text = $"v{version.Major}.{version.Minor}.{version.Build}";

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

    /// <summary>标题栏拖动窗口（try-catch：非主键按下时 DragMove 会抛）</summary>
    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        try
        {
            DragMove();
        }
        catch
        {
            // 忽略非拖拽场景
        }
    }

    /// <summary>标题栏关闭按钮</summary>
    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    /// <summary>为 ToggleSwitch 接线旋钮位移动画（Checked/Unchecked）</summary>
    private static void WireToggleAnimation(ToggleButton toggle)
    {
        toggle.Checked += (_, _) => AnimateKnob(toggle, true);
        toggle.Unchecked += (_, _) => AnimateKnob(toggle, false);
    }

    /// <summary>动画旋钮平移：通过 Template.FindName 获取模板内元素，绕开模板触发器名称解析限制</summary>
    private static void AnimateKnob(ToggleButton toggle, bool on)
    {
        if (toggle.Template.FindName("Knob", toggle) is not Ellipse knob ||
            knob.RenderTransform is not TranslateTransform translate)
            return;

        var animation = new DoubleAnimation(on ? 20 : 0, TimeSpan.FromMilliseconds(120))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };
        translate.BeginAnimation(TranslateTransform.XProperty, animation);
    }

    /// <summary>
    ///     Win11 22H2+ 启用深色 Mica；失败/旧系统静默回退到 XAML 里的 #202020。
    ///     先设深色模式再设 Mica backdrop（顺序反了 Mica 会是浅色），
    ///     且只有 DwmSetWindowAttribute 返回成功才把背景交给 DWM。
    /// </summary>
    private void TryEnableMica()
    {
        try
        {
            if (Environment.OSVersion.Version.Build < 22621)
                return; // Mica（DWMSBT_MAINWINDOW）需要 22H2

            var hwnd = new WindowInteropHelper(this).Handle;

            // DWMWA_USE_IMMERSIVE_DARK_MODE：先申请深色模式
            var darkMode = 1;
            DwmSetWindowAttribute(hwnd, 20, ref darkMode, sizeof(int));

            // DWMWA_SYSTEMBACKDROP_TYPE = DWMSBT_MAINWINDOW
            var backdrop = 2;
            var hr = DwmSetWindowAttribute(hwnd, 38, ref backdrop, sizeof(int));

            // 只有调用成功（HRESULT ≥ 0）才把背景交给 DWM，否则保持回退色
            Background = hr >= 0
                ? Brushes.Transparent
                : new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x20));
        }
        catch
        {
            // 回退到 XAML 里的 #202020
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
