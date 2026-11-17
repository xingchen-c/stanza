using System.Reflection;

namespace Stanza;

internal static class AppVersion
{
    public static string Full { get; } = typeof(AppVersion).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;

    public static string Display { get; } = GetDisplayVersion(Full);

    // 界面保留预发布后缀；日志与崩溃报告使用含提交号的完整版本。
    internal static string GetDisplayVersion(string version) => version.Split('+')[0];
}
