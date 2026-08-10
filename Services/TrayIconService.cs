using System.Drawing;
using System.Windows.Forms;
using Application = System.Windows.Application;

namespace FluentLyrics.Services;

/// <summary>托盘图标服务：右键菜单提供显示/隐藏歌词、设置、打开日志目录、退出</summary>
public sealed class TrayIconService : IDisposable
{
    private readonly NotifyIcon _notifyIcon;

    private readonly ToolStripMenuItem? _adjustMenuItem;

    public TrayIconService()
    {
        _notifyIcon = new NotifyIcon
        {
            Icon = LoadAppIcon(),
            Text = "FluentLyrics 任务栏歌词",
            Visible = true
        };

        var menu = new ContextMenuStrip();
        menu.Items.Add("显示/隐藏歌词", null, (_, _) => ShowHideRequested?.Invoke());
        menu.Items.Add("设置", null, (_, _) => SettingsRequested?.Invoke());
        menu.Items.Add("打开日志目录", null, (_, _) => OpenLogDirectoryRequested?.Invoke());
        menu.Items.Add("清除歌词缓存", null, (_, _) => ClearCacheRequested?.Invoke());
        menu.Items.Add("歌词选择…", null, (_, _) => LyricsSelectionRequested?.Invoke());
        _adjustMenuItem = new ToolStripMenuItem("调整歌词位置…", null, (_, _) => AdjustPositionRequested?.Invoke());
        menu.Items.Add(_adjustMenuItem);
        menu.Items.Add("重置歌词位置", null, (_, _) => ResetPositionRequested?.Invoke());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => ExitRequested?.Invoke());
        _notifyIcon.ContextMenuStrip = menu;

        _notifyIcon.DoubleClick += (_, _) => ShowHideRequested?.Invoke();
    }

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
    }

    /// <summary>请求切换卡片显隐</summary>
    public event Action? ShowHideRequested;

    /// <summary>请求退出程序</summary>
    public event Action? ExitRequested;

    /// <summary>请求打开设置窗口</summary>
    public event Action? SettingsRequested;

    /// <summary>请求打开日志目录（资源管理器）</summary>
    public event Action? OpenLogDirectoryRequested;

    /// <summary>请求清除歌词缓存</summary>
    public event Action? ClearCacheRequested;

    /// <summary>请求打开歌词候选选择（手动选歌词版本）</summary>
    public event Action? LyricsSelectionRequested;

    /// <summary>请求进入/退出"调整歌词位置"模式</summary>
    public event Action? AdjustPositionRequested;

    /// <summary>请求重置手动位置偏移</summary>
    public event Action? ResetPositionRequested;

    /// <summary>加载嵌入资源里的自定义图标；失败时回退系统图标</summary>
    private static Icon LoadAppIcon()
    {
        try
        {
            var stream = Application.GetResourceStream(
                new Uri("pack://application:,,,/Assets/app.ico"))?.Stream;
            if (stream is not null)
                return new Icon(stream);
        }
        catch
        {
            // ignored
        }

        return SystemIcons.Application;
    }

    /// <summary>切换"调整歌词位置"菜单项文案（调整中显示退出/取消）</summary>
    public void SetAdjusting(bool adjusting)
    {
        if (_adjustMenuItem is not null)
            _adjustMenuItem.Text = adjusting ? "完成调整（保存）" : "调整歌词位置…";
    }
}
