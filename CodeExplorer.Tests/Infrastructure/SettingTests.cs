using CodeExplorer.Infrastructure;
using Xunit;

namespace CodeExplorer.Tests;

/// <summary>
///     The data directory every on-disk component is placed under. Absent configuration must still
///     answer, because a plain <c>dotnet run</c> with an empty <c>appsettings</c> is a complete server
///     (CODING_STANDARDS, Dependencies).
/// </summary>
public sealed class SettingTests
{
    [Fact]
    public void The_data_directory_is_data_where_nothing_configures_it() =>
        Assert.Equal("data", Setting.DataDirectory(Settings.Of([])));

    [Fact]
    public void The_data_directory_is_the_configured_one_where_it_is_set() =>
        Assert.Equal("/srv/codeexplorer",
            Setting.DataDirectory(Settings.Of(new Dictionary<string, string?>
                { ["Storage:DataDirectory"] = "/srv/codeexplorer" })));
}
