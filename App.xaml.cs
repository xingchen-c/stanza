using System.Configuration;
using System.Data;
using System.Threading;
using System.Windows;

namespace FluentLyrics;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    private static Mutex? _singleInstanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        // 单实例保护：防止两个歌词窗口叠在任务栏上
        _singleInstanceMutex = new Mutex(true, @"Local\FluentLyrics_SingleInstance", out bool createdNew);
        if (!createdNew)
        {
            Shutdown();
            return;
        }

        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            _singleInstanceMutex?.ReleaseMutex();
        }
        catch
        {
            // 不持有互斥体时释放会抛异常，忽略
        }

        base.OnExit(e);
    }
}
