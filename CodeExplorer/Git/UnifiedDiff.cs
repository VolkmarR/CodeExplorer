namespace CodeExplorer.Git;

/// <summary>
///     One edit a commit made to a file, as the position it applies at in the file before the commit
///     and how many lines it took out and put in. A patch is a list of these in ascending order, and
///     replaying them onto the previous attribution is how a build attributes without a blame
///     (ADR-0007). Context lines are not carried: they are the lines between edits, and the replay
///     copies them by position.
/// </summary>
/// <param name="OldLine">
///     0-based index into the file before the commit of the first line removed — or, for a pure
///     insertion, of the line the new ones go in front of; equal to the old line count for an append.
/// </param>
/// <param name="Deleted">Lines removed at that position.</param>
/// <param name="Added">Lines the commit put in their place.</param>
public sealed record LineEdit(int OldLine, int Deleted, int Added);

/// <summary>
///     The one rule of the unified-diff hunk header that placing an edit needs. <see cref="NativeDiff" />
///     reads the same two numbers off libgit2's hunk structs (#292), and the tests' reading of the
///     rendered patch, which they hold that one to, places its hunks by this too.
/// </summary>
internal static class UnifiedDiff
{
    /// <summary>
    ///     The 0-based old position a hunk's body starts at, from its <c>@@ -a[,b] +c[,d] @@</c> header.
    ///     <c>a</c> is 1-based, so the body starts at <c>a - 1</c> — except when <c>b</c> is 0: a hunk
    ///     that removes nothing names the line <em>before</em> the insertion, so its body starts at
    ///     <c>a</c>. The spike that produced this code got that wrong first and attributed every added
    ///     file to nothing; it is the one rule of the format worth a sentence.
    /// </summary>
    public static int OldPosition(int start, int count) => count == 0 ? start : start - 1;
}
