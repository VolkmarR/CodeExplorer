using CodeExplorer.Git;
using LibGit2Sharp;
using Xunit;

namespace CodeExplorer.Tests;

/// <summary>
///     The word <c>commit_files.change_kind</c> stores. The spelled-out literals exist only to save an
///     allocation per path, so every kind must still read as the enum name lower-cased.
/// </summary>
public sealed class ChangeKindNameTests
{
    public static TheoryData<ChangeKind> Kinds() => new(Enum.GetValues<ChangeKind>());

    [Theory]
    [MemberData(nameof(Kinds))]
    public void A_change_kind_is_stored_as_its_name_lower_cased(ChangeKind kind) =>
        Assert.Equal(kind.ToString().ToLowerInvariant(), LocalCopy.KindName(kind));
}
