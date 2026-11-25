using Microsoft.Win32;
namespace Stanza.Infrastructure.Settings;

public sealed class WindowsAutoStart : IAutoStart
{
    /// <summary>应用开机自启（注册表 HKCU Run 键）</summary>
    public void Apply(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Run", true);
            if (key is null)
                return;

            if (enabled)
                key.SetValue("Stanza", $"\"{Environment.ProcessPath}\"");
            else
                key.DeleteValue("Stanza", false);
        }
        catch
        {
            // 注册表操作失败不影响主流程
        }
    }
}
