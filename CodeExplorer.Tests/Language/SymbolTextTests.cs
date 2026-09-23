using Xunit;

namespace CodeExplorer.Tests;

/// <summary>
///     How a literal is written for RE2. Unit tests for the same reason as
///     <see cref="LanguageAnalyzerTests" />: the thing under test is a string, and what the pattern
///     finds in a real index is covered end-to-end in <see cref="ReferenceTests" />.
/// </summary>
public sealed class SymbolTextTests
{
    [Theory]
    [InlineData("OrderStatus", "OrderStatus")]
    [InlineData("", "")]
    [InlineData("a b#c", "a b#c")]
    [InlineData(@"\.+*?()|[]{}^$", @"\\\.\+\*\?\(\)\|\[\]\{\}\^\$")]
    [InlineData("List<T>.Count()", @"List<T>\.Count\(\)")]
    [InlineData("operator+=", @"operator\+=")]
    public void A_literal_escapes_exactly_the_metacharacters(string text, string expected) =>
        Assert.Equal(expected, SymbolText.Re2Literal(text));

    [Fact]
    public void A_literal_with_nothing_to_escape_is_the_text_itself()
    {
        string text = "OrderStatus";
        Assert.Same(text, SymbolText.Re2Literal(text));
    }
}
