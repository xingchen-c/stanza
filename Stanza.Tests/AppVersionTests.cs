using System.Reflection;
using Xunit;

namespace Stanza.Tests;

public class AppVersionTests
{
    [Theory]
    [InlineData("0.3.0", "0.3.0")]
    [InlineData("0.3.0+abc123", "0.3.0")]
    [InlineData("0.3.0-beta.1", "0.3.0-beta.1")]
    [InlineData("0.3.0-beta.1+abc123", "0.3.0-beta.1")]
    [InlineData("0.3.0-rc.2+abc123", "0.3.0-rc.2")]
    public void 界面版本保留预发布后缀但隐藏构建信息(string fullVersion, string expected)
    {
        Assert.Equal(expected, AppVersion.GetDisplayVersion(fullVersion));
    }

    [Fact]
    public void 版本来自应用程序集而非测试程序集或手填字符串()
    {
        var generatedVersion = typeof(AppVersion).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;

        Assert.Equal(generatedVersion, AppVersion.Full);
        Assert.Equal(AppVersion.GetDisplayVersion(generatedVersion), AppVersion.Display);
    }
}
