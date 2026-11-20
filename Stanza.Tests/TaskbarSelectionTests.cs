using Stanza.Models;
using Stanza.Services.Taskbar;
using Xunit;
using TaskbarWindow = Stanza.Services.Taskbar.TaskbarEnumerator.TaskbarWindow;

namespace Stanza.Tests;

public class TaskbarSelectionTests
{
    private static readonly TaskbarWindow Primary = new(new IntPtr(1), new PixelRect(0, 0, 3120, 72), true);
    private static readonly TaskbarWindow Secondary = new(new IntPtr(2), new PixelRect(-3840, 0, 0, 72), false);

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
}
