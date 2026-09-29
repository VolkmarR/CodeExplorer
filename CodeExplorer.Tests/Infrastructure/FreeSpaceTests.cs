using CodeExplorer.Infrastructure;
using Xunit;

namespace CodeExplorer.Tests;

/// <summary>
///     Which mount a path is measured on (<see cref="FreeSpace.MountHolding" />). Driven with mount lists
///     rather than the machine's, because the suite runs on Windows and the case that was wrong is a
///     Linux container whose data directory is a volume mounted below <c>/</c>.
/// </summary>
public sealed class FreeSpaceTests
{
    private static readonly string[] _linux = ["/", "/proc", "/data", "/data/clones", "/database", "/mnt/"];

    [Theory]
    [InlineData("/data/indexes/alpha.duckdb", "/data")]
    [InlineData("/data", "/data")]
    [InlineData("/data/clones/alpha/one.git", "/data/clones")]
    [InlineData("/database/x", "/database")]
    [InlineData("/datastore/x", "/")]
    [InlineData("/app/data", "/")]
    [InlineData("/mnt/volume", "/mnt/")]
    public void A_linux_path_is_measured_on_the_longest_mount_that_holds_it(string path, string mount)
    {
        Assert.Equal(mount, FreeSpace.MountHolding(path, _linux, StringComparison.Ordinal));
    }

    [Fact]
    public void Linux_mount_points_are_case_sensitive()
    {
        Assert.Equal("/", FreeSpace.MountHolding("/Data/x", _linux, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(@"D:\CodeExplorer\data", @"D:\")]
    [InlineData(@"d:\codeexplorer", @"D:\")]
    [InlineData(@"C:\inetpub", @"C:\")]
    [InlineData(@"C:\", @"C:\")]
    public void A_windows_path_is_measured_on_its_drive(string path, string mount)
    {
        Assert.Equal(mount, FreeSpace.MountHolding(path, [@"C:\", @"D:\"], StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_path_no_mount_holds_has_none()
    {
        Assert.Null(FreeSpace.MountHolding(@"E:\data", [@"C:\", @"D:\"], StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void The_machine_answers_for_a_folder_that_does_not_exist_yet()
    {
        Assert.True(FreeSpace.Available(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))) > 0);
    }
}
