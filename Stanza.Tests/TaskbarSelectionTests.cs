using Stanza.Core.Models;
using Stanza.Desktop.Taskbar;
using Xunit;
using TaskbarWindow = Stanza.Desktop.Taskbar.TaskbarEnumerator.TaskbarWindow;

namespace Stanza.Tests;

public class TaskbarSelectionTests
{
    private static readonly IntPtr MainMonitor = new(101);
    private static readonly IntPtr LeftMonitor = new(102);
    private static readonly TaskbarWindow Primary = new(new IntPtr(1), new PixelRect(0, 0, 3120, 72), true, MainMonitor);
    private static readonly TaskbarWindow Secondary = new(new IntPtr(2), new PixelRect(-2560, 0, 0, 48), false, LeftMonitor);

    [Fact]
    public void 首次定位不随枚举顺序选择副屏()
    {
        Assert.Equal(Primary, TaskbarEnumerator.OrderForSelection([Secondary, Primary], IntPtr.Zero)[0]);
        Assert.Equal(Primary, TaskbarEnumerator.OrderForSelection([Primary, Secondary], IntPtr.Zero)[0]);
    }

    [Fact]
    public void 已定位的任务栏优先于枚举顺序和主副标识()
    {
        Assert.Equal(Secondary, TaskbarEnumerator.OrderForSelection([Primary, Secondary], Secondary.Handle)[0]);
        Assert.Equal(Secondary, TaskbarEnumerator.OrderForSelection([Secondary, Primary], Secondary.Handle)[0]);
    }

    [Fact]
    public void 原任务栏移除后重新选择主任务栏()
    {
        Assert.Equal(Primary, TaskbarEnumerator.OrderForSelection([Primary], Secondary.Handle)[0]);
        Assert.Empty(TaskbarEnumerator.OrderForSelection([], Secondary.Handle));
    }

    [Fact]
    public void 无主任务栏时按稳定顺序兜底()
    {
        var another = new TaskbarWindow(new IntPtr(3), new PixelRect(0, 2080, 3120, 2152), false);
        Assert.Equal(Secondary, TaskbarEnumerator.OrderForSelection([another, Secondary], IntPtr.Zero)[0]);
        Assert.Equal(Secondary, TaskbarEnumerator.OrderForSelection([Secondary, another], IntPtr.Zero)[0]);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void 开始菜单让目标任务栏暂时不被枚举时不改选另一屏(bool primaryTarget)
    {
        var target = primaryTarget ? Primary : Secondary;
        var other = primaryTarget ? Secondary : Primary;
        var selected = TaskbarEnumerator.OrderForSelection([other], target.Handle, target.Monitor, true);
        Assert.Empty(selected);

        Assert.Equal(target, TaskbarEnumerator.OrderForSelection(
            [other, target], target.Handle, target.Monitor, true)[0]);
    }

    [Fact]
    public void 同屏任务栏重建后沿用显示器而非旧句柄()
    {
        var replacement = Secondary with { Handle = new IntPtr(3) };
        Assert.Equal(replacement, TaskbarEnumerator.OrderForSelection(
            [Primary, replacement], Secondary.Handle, LeftMonitor, true)[0]);
    }

    [Fact]
    public void 旧句柄出现在另一屏时仍优先原显示器()
    {
        var moved = Secondary with { Rect = Primary.Rect, Monitor = MainMonitor };
        var replacement = Secondary with { Handle = new IntPtr(3) };
        Assert.Equal(replacement, TaskbarEnumerator.OrderForSelection(
            [moved, replacement], Secondary.Handle, LeftMonitor, true)[0]);
    }

    [Fact]
    public void 原显示器确实断开后才回退到主任务栏()
        => Assert.Equal(Primary, TaskbarEnumerator.OrderForSelection(
            [Primary], Secondary.Handle, LeftMonitor, false)[0]);
}
