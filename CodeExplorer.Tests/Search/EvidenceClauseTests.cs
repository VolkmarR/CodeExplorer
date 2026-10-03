using CodeExplorer.Language;
using CodeExplorer.Search;
using Xunit;

namespace CodeExplorer.Tests;

/// <summary>
///     The clause every reply built on language answers ends with (ADR-0008). Only the textual branch
///     is reachable through a tool until a parser-backed analyser is registered, so the other two are
///     asserted here, where a reply that called a parsed answer textual, or a textual one parsed, would
///     otherwise first show up the day the first parser does.
/// </summary>
public sealed class EvidenceClauseTests
{
    [Theory]
    [InlineData(new Evidence[0], "textual")]
    [InlineData(new[] { Evidence.Text, Evidence.Text }, "textual")]
    [InlineData(new[] { Evidence.Parsed, Evidence.Parsed }, "parsed")]
    [InlineData(new[] { Evidence.Text, Evidence.Parsed }, "mixed")]
    [InlineData(new[] { Evidence.Parsed, Evidence.Text }, "mixed")]
    public void The_clause_follows_how_every_answer_was_reached(Evidence[] evidence, string expected)
    {
        Assert.Equal(expected, EvidenceClause.Of(evidence, "textual", "parsed", "mixed"));
    }
}
