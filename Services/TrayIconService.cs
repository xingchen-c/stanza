using System.Drawing;
using System.Windows.Forms;

namespace FluentLyrics.Services;

/// <summary>托盘图标服务：右键菜单提供显示/隐藏歌词、设置、打开日志目录、退出</summary>
public sealed class TrayIconService : IDisposable
{
    private readonly NotifyIcon _notifyIcon;

    /// <summary>请求切换卡片显隐</summary>
    public event Action? ShowHideRequested;

    /// <summary>请求退出程序</summary>
    public event Action? ExitRequested;

    /// <summary>请求打开设置窗口</summary>
    public event Action? SettingsRequested;

    /// <summary>请求打开日志目录（资源管理器）</summary>
    public event Action? OpenLogDirectoryRequested;

    public TrayIconService()
    {
        _notifyIcon = new NotifyIcon
        {
            Icon = LoadAppIcon(),
            Text = "FluentLyrics 任务栏歌词",
            Visible = true,
        };

        var menu = new ContextMenuStrip();
        menu.Items.Add("显示/隐藏歌词", null, (_, _) => ShowHideRequested?.Invoke());
        menu.Items.Add("设置", null, (_, _) => SettingsRequested?.Invoke());
        menu.Items.Add("打开日志目录", null, (_, _) => OpenLogDirectoryRequested?.Invoke());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => ExitRequested?.Invoke());
        _notifyIcon.ContextMenuStrip = menu;

        _notifyIcon.DoubleClick += (_, _) => ShowHideRequested?.Invoke();
    }

    /// <summary>加载嵌入资源里的自定义图标；失败时回退系统图标</summary>
    private static Icon LoadAppIcon()
    {
        try
        {
            var stream = System.Windows.Application.GetResourceStream(
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

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
    }
}
