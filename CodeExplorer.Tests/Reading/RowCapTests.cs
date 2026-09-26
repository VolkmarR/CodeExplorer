using CodeExplorer.Reading;
using Xunit;

namespace CodeExplorer.Tests;

/// <summary>
///     The one step every capped read shares: ask for a row past the cap, and if it came, drop it and
///     say the answer was cut. What it has to prove is that landing on the cap exactly is not a cut.
/// </summary>
public sealed class RowCapTests
{
    [Fact]
    public void A_read_that_fills_its_limit_is_a_cut()
    {
        var rows = Enumerable.Range(1, RowCap.Limit(3)).ToList();

        Assert.True(RowCap.Trim(rows, 3));
    }

    [Theory]
    [InlineData(2, false, 2)]
    [InlineData(3, false, 3)]
    [InlineData(4, true, 3)]
    public void Only_a_row_past_the_cap_is_a_cut(int read, bool cut, int kept)
    {
        var rows = Enumerable.Range(1, read).ToList();

        Assert.Equal(cut, RowCap.Trim(rows, 3));
        Assert.Equal(Enumerable.Range(1, kept), rows);
    }
}
