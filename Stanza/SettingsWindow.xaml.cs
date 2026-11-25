using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using Stanza.Core.Models;
using Stanza.Interop;

namespace Stanza;

/// <summary>设置窗口：编辑并保存 AppSettings</summary>
public partial class SettingsWindow
{
    // 保存设置时保留与界面无关的字段（如手动位置偏移），避免被默认值覆盖
    private readonly ISettingsStore _settingsStore;
    private readonly IAutoStart _autoStart;
    private readonly AppSettings _baseSettings;
    private readonly SettingsViewModel _viewModel;

    /// <summary>用当前设置初始化窗口，并把控件事件绑定到实时预览与保存流程。</summary>
    public SettingsWindow(AppSettings settings, ISettingsStore settingsStore, IAutoStart autoStart)
    {
        InitializeComponent();
        _settingsStore = settingsStore;
        _autoStart = autoStart;
        SourceInitialized += (_, _) => TryEnableMica();
        _baseSettings = settings;
        _viewModel = SettingsViewModel.FromSettings(settings);

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

        VersionText.Text = $"v{AppVersion.Display}";

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

        foreach (ComboBoxItem item in CompatibilityCombo.Items)
            if (item.Tag?.ToString() == settings.CompatibilityMode)
            {
                CompatibilityCombo.SelectedItem = item;
                break;
            }

        foreach (ComboBoxItem item in AnimationCombo.Items)
            if (item.Tag?.ToString() == settings.AnimationSpeed)
            {
                AnimationCombo.SelectedItem = item;
                break;
            }

        // 初始值填充完成后再接预览事件，避免初始化时误触发
        WirePreviewEvents();
    }

    /// <summary>保存成功后触发（主窗口据此重新应用设置）</summary>
    public event Action? Saved;

    /// <summary>任一设置项变化时触发（主窗口据此实时预览，不落盘）</summary>
    public event Action<AppSettings>? PreviewChanged;

    private void AutoWidth_Changed(object sender, RoutedEventArgs e)
    {
        WidthSlider.IsEnabled = AutoWidthCheck.IsChecked != true;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var settings = BuildSettings();

        // 白名单至少勾选一项：全不勾会导致定位没有依据，禁止保存
        if (!_viewModel.IsValid)
        {
            MessageBox.Show(this, "至少勾选一种任务栏定位方式。", "Stanza",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _settingsStore.Save(settings);
        _autoStart.Apply(settings.AutoStart);
        Saved?.Invoke();
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    /// <summary>从当前控件状态构造 AppSettings（保存与实时预览共用）</summary>
    private AppSettings BuildSettings()
    {
        SyncViewModel();
        return _viewModel.ToSettings(_baseSettings);
    }

    private void SyncViewModel()
    {
        var whitelist = new List<string>();
        if (FluentFlyoutCheck.IsChecked == true)
            whitelist.Add("FluentFlyout");
        if (IndependentCheck.IsChecked == true)
            whitelist.Add("Independent");

        _viewModel.FontSize = FontSizeSlider.Value;
        _viewModel.LyricColor = (ColorCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "#FFFFFFFF";
        _viewModel.LyricOffsetMs = (int)OffsetSlider.Value;
        _viewModel.ClickThrough = ClickThroughCheck.IsChecked == true;
        _viewModel.ShowMediaControls = MediaControlsCheck.IsChecked == true;
        _viewModel.HideOnPause = HideOnPauseCheck.IsChecked == true;
        _viewModel.AutoStart = AutoStartCheck.IsChecked == true;
        _viewModel.TaskbarLocatorWhitelist = whitelist;
        _viewModel.PlacementDirection = (DirectionCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "Auto";
        _viewModel.AutoCardWidth = AutoWidthCheck.IsChecked == true;
        _viewModel.CardWidth = WidthSlider.Value;
        _viewModel.CompatibilityMode = (CompatibilityCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "Auto";
        _viewModel.AnimationSpeed = (AnimationCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "Normal";
    }

    /// <summary>任一设置项变化时触发预览（白名单为空属于无效状态，跳过）</summary>
    private void OnPreviewChanged()
    {
        var settings = BuildSettings();
        if (!_viewModel.IsValid)
            return;
        PreviewChanged?.Invoke(settings);
    }

    /// <summary>为所有设置控件接上预览事件（在初始值填充完成后调用）</summary>
    private void WirePreviewEvents()
    {
        FontSizeSlider.ValueChanged += (_, _) => OnPreviewChanged();
        OffsetSlider.ValueChanged += (_, _) => OnPreviewChanged();
        WidthSlider.ValueChanged += (_, _) => OnPreviewChanged();

        ColorCombo.SelectionChanged += (_, _) => OnPreviewChanged();
        DirectionCombo.SelectionChanged += (_, _) => OnPreviewChanged();
        CompatibilityCombo.SelectionChanged += (_, _) => OnPreviewChanged();
        AnimationCombo.SelectionChanged += (_, _) => OnPreviewChanged();

        WireTogglePreview(ClickThroughCheck);
        WireTogglePreview(MediaControlsCheck);
        WireTogglePreview(HideOnPauseCheck);
        WireTogglePreview(AutoStartCheck);
        WireTogglePreview(FluentFlyoutCheck);
        WireTogglePreview(IndependentCheck);
        WireTogglePreview(AutoWidthCheck);
    }

    private void WireTogglePreview(ToggleButton toggle)
    {
        toggle.Checked += (_, _) => OnPreviewChanged();
        toggle.Unchecked += (_, _) => OnPreviewChanged();
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
            // 该属性在少数系统版本可能被拒绝；不能因此跳过后续 backdrop。
            var darkMode = 1;
            NativeMethods.DwmSetWindowAttribute(hwnd, 20, ref darkMode, sizeof(int));

            // DWMWA_SYSTEMBACKDROP_TYPE = DWMSBT_MAINWINDOW
            var backdrop = 2;
            var hr = NativeMethods.DwmSetWindowAttribute(hwnd, 38, ref backdrop, sizeof(int));

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

}
