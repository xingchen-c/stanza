namespace Stanza.Models;

/// <summary>应用设置（JSON 持久化）</summary>
public sealed class AppSettings
{
    /// <summary>歌词字号（DIP）</summary>
    public double FontSize { get; set; } = 15;

    /// <summary>歌词颜色（ARGB 十六进制，如 #FFFFFFFF）</summary>
    public string LyricColor { get; set; } = "#FFFFFFFF";

    /// <summary>歌词全局时间偏移（毫秒，正数 = 歌词提前显示）</summary>
    public int LyricOffsetMs { get; set; }

    /// <summary>鼠标点击穿透</summary>
    public bool ClickThrough { get; set; } = true;

    /// <summary>开机自启</summary>
    public bool AutoStart { get; set; }

    /// <summary>
    ///     任务栏定位策略白名单（定位器 Key 列表）：
    ///     FluentFlyout = 贴 FluentFlyout 小组件；Independent = 独立定位（不依赖外部组件）。
    ///     至少勾选一项；按列表顺序（固定优先级 FluentFlyout &gt; Independent）依次尝试。
    /// </summary>
    public List<string> TaskbarLocatorWhitelist { get; set; } = ["FluentFlyout", "Independent"];

    /// <summary>
    ///     放置方向（相对锚点组件）：
    ///     Auto = 组件在任务栏左半区 → 卡片贴其右侧，右半区 → 贴其左侧；
    ///     Right = 始终贴组件右侧；Left = 始终贴组件左侧。
    /// </summary>
    public string PlacementDirection { get; set; } = "Auto";

    /// <summary>手动位置偏移 X（DIP，相对自动锚点区域；拖动调整后保存，0 = 不偏移）</summary>
    public double ManualOffsetX { get; set; }

    /// <summary>手动位置偏移 Y（DIP，相对自动锚点区域）</summary>
    public double ManualOffsetY { get; set; }

    /// <summary>歌词栏宽度是否自动适配定位区域（true = 跟随锚点/独立定位计算出的宽度）</summary>
    public bool AutoCardWidth { get; set; } = true;

    /// <summary>歌词栏手动宽度（DIP，仅 AutoCardWidth=false 时生效）</summary>
    public double CardWidth { get; set; } = 600;

    /// <summary>是否显示封面与播放控制（媒体区：封面在左、上一首/播放暂停/下一首在右）</summary>
    public bool ShowMediaControls { get; set; } = true;

    /// <summary>暂停时隐藏歌词卡片（false = 暂停时保持显示）</summary>
    public bool HideOnPause { get; set; } = true;

    /// <summary>
    ///     渲染兼容模式（低性能设备 / 无显卡驱动，软件渲染时透明窗口 + 阴影会糊成一片并闪烁）：
    ///     Auto = 检测到软件渲染（RenderCapability.Tier=0）时自动降级；ForceOn = 强制降级；ForceOff = 禁用降级。
    /// </summary>
    public string CompatibilityMode { get; set; } = "Auto";

    /// <summary>
    ///     动画速度（翻译行 / 悬停面板渐入渐出）：
    ///     Off = 关闭（瞬时切换）；Fast / Normal / Slow = 快 / 标准 / 慢。
    /// </summary>
    public string AnimationSpeed { get; set; } = "Normal";
}
