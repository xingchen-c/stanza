using Stanza.Models;
using Xunit;

namespace Stanza.Tests;

public class SettingsViewModelTests
{
    [Fact]
    public void Roundtrip_preserves_manual_position_fields()
    {
        var baseSettings = new AppSettings
        {
            ManualOffsetX = 12.5,
            ManualOffsetY = -8.25
        };
        var viewModel = SettingsViewModel.FromSettings(baseSettings);

        viewModel.FontSize = 20;
        viewModel.TaskbarLocatorWhitelist = ["FluentFlyout"];

        var result = viewModel.ToSettings(baseSettings);

        Assert.Equal(20, result.FontSize);
        Assert.Equal(12.5, result.ManualOffsetX);
        Assert.Equal(-8.25, result.ManualOffsetY);
        Assert.Equal(["FluentFlyout"], result.TaskbarLocatorWhitelist);
    }

    [Fact]
    public void Empty_locator_whitelist_is_invalid()
    {
        var viewModel = SettingsViewModel.FromSettings(new AppSettings());
        viewModel.TaskbarLocatorWhitelist = [];

        Assert.False(viewModel.IsValid);
    }

    [Fact]
    public void 保存设置不应重置日志级别()
    {
        var baseSettings = new AppSettings { LogLevel = "Debug" };
        var viewModel = SettingsViewModel.FromSettings(baseSettings);

        Assert.Equal("Debug", viewModel.ToSettings(baseSettings).LogLevel);
    }
}
