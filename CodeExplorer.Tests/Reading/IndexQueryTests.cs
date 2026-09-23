using DuckDB.NET.Data;
using Xunit;

namespace CodeExplorer.Tests;

/// <summary>
///     The one SQL string literal every index writer inlines a path or a slug through. What it has to
///     prove is that DuckDB reads back exactly the text that went in, quote marks included.
/// </summary>
public sealed class IndexQueryTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "CodeExplorer.Tests", Guid.NewGuid().ToString("N"));

    public IndexQueryTests() => Directory.CreateDirectory(_root);

    public void Dispose() => TestHost.DeleteTree(_root);

    [Theory]
    [InlineData("plain")]
    [InlineData(@"C:\data\it's here")]
    [InlineData("''")]
    [InlineData("")]
    public async Task A_literal_reads_back_as_the_text_it_was_made_from(string text)
    {
        await using var connection = new DuckDBConnection($"Data Source={Path.Combine(_root, "literal.duckdb")}");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {IndexQuery.Literal(text)}";

        Assert.Equal(text, await command.ExecuteScalarAsync(TestContext.Current.CancellationToken));
    }
}
