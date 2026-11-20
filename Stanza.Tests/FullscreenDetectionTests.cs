using Stanza.Models;
using Stanza.Services.Desktop;
using Xunit;

namespace Stanza.Tests;

public class FullscreenDetectionTests
{
    private static readonly PixelRect MainMonitor = new(0, 0, 3120, 2080);
    private static readonly PixelRect LeftMonitor = new(-3840, 0, 0, 2160);

    [Theory]
    [InlineData(0, 0, 3120, 2080, true)]
    [InlineData(-8, -8, 3128, 2088, true)]
    [InlineData(0, 0, 3120, 72, false)]
    [InlineData(0, 72, 3120, 2080, false)]
    [InlineData(0, 0, 3120, 2008, false)]
    [InlineData(72, 0, 3120, 2080, false)]
    [InlineData(0, 0, 3048, 2080, false)]
    public void 只有覆盖整个显示器才算全屏(int left, int top, int right, int bottom, bool expected)
    {
        var window = new PixelRect(left, top, right, bottom);

        Assert.Equal(expected, ForegroundFullscreenDetector.IsFullscreenWindow(
            window, MainMonitor, "Chrome_WidgetWin_1", "chrome"));
    }

    [Theory]
    [InlineData("Windows.UI.Core.CoreWindow", "StartMenuExperienceHost")]
    [InlineData("Windows.UI.Core.CoreWindow", "SearchHost")]
    [InlineData("Windows.UI.Core.CoreWindow", "ShellExperienceHost")]
    [InlineData("XamlExplorerHostIslandWindow", "explorer")]
    [InlineData("XamlExplorerHostIslandWindow_WASDK", "explorer")]
    [InlineData("TopLevelWindowForOverflowXamlIsland", "explorer")]
    [InlineData("任意外壳窗口类", "STARTMENUEXPERIENCEHOST")]
    [InlineData("Shell_TrayWnd", "explorer")]
    [InlineData("Shell_SecondaryTrayWnd", "explorer")]
    [InlineData("Progman", "explorer")]
    [InlineData("WorkerW", "explorer")]
    public void 铺满屏幕的系统外壳不算全屏应用(string windowClass, string processName)
    {
        Assert.False(ForegroundFullscreenDetector.IsFullscreenWindow(
            MainMonitor, MainMonitor, windowClass, processName));
    }

    [Theory]
    [InlineData("Windows.UI.Core.CoreWindow", "VideoPlayer")]
    [InlineData("Chrome_WidgetWin_1", "chrome")]
    [InlineData("游戏窗口", "Game")]
    [InlineData("CabinetWClass", "explorer")]
    [InlineData("ExploreWClass", "explorer")]
    [InlineData("游戏窗口", null)]
    public void 普通应用全屏不因共用窗口类或进程读取失败而漏判(string windowClass, string? processName)
    {
        Assert.True(ForegroundFullscreenDetector.IsFullscreenWindow(
            MainMonitor, MainMonitor, windowClass, processName));
    }

    [Fact]
    public void 另一块屏幕全屏不隐藏当前屏幕的卡片()
    {
        Assert.False(ForegroundFullscreenDetector.IsFullscreenWindow(
            LeftMonitor, MainMonitor, "游戏窗口", "Game"));
        Assert.False(ForegroundFullscreenDetector.IsFullscreenWindow(
            MainMonitor, LeftMonitor, "游戏窗口", "Game"));
        Assert.True(ForegroundFullscreenDetector.IsFullscreenWindow(
            LeftMonitor, LeftMonitor, "游戏窗口", "Game"));
    }

    [Fact]
    public void 跨屏全屏覆盖当前显示器时仍应隐藏()
    {
        var spanning = new PixelRect(-3840, 0, 3120, 2160);

        Assert.True(ForegroundFullscreenDetector.IsFullscreenWindow(
            spanning, MainMonitor, "游戏窗口", "Game"));
        Assert.True(ForegroundFullscreenDetector.IsFullscreenWindow(
            spanning, LeftMonitor, "游戏窗口", "Game"));
    }
}
