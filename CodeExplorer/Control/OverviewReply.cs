using System.Globalization;
using System.Text;
using CodeExplorer.Infrastructure;
using CodeExplorer.Reading;

namespace CodeExplorer.Control;

/// <summary>
///     An <see cref="IndexOverview" /> as <c>project_overview</c> hands it to an agent. It is its own
///     type because it is all rendering and no reading: <see cref="ProjectTools" /> is where the tools
///     are declared and where the index is opened, and a hundred lines of formatting living there
///     would make that class change both when a tool changes and when the overview's shape does.
///     Every section is written the way the tool that owns the same question writes it — the ranking
///     like <c>hot_files</c>, the counts like <c>repo_info</c> — so an agent reading two of them is
///     reading one format.
/// </summary>
internal static class OverviewReply
{
    public static string Render(Project project, IReadOnlyList<IndexedRepository> repositories,
        IndexOverview overview)
    {
        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture,
            $"Project '{project.Slug}' ({project.Name}): {repositories.Count} {ToolReply.Plural(repositories.Count, "repository", "repositories")}, "
            + $"{repositories.Sum(r => r.FileCount):N0} files, {repositories.Sum(r => r.LineCount):N0} lines.\n");

        // No URL: what the server cloned from is often a path on its own disk, which an agent cannot
        // open and should not quote. The slug is how every tool names the repository.
        text.Append("\nRepositories\n");
        foreach (var repository in repositories)
            text.Append(CultureInfo.InvariantCulture,
                $"  {repository.Slug}  at {repository.HeadCommit[..Math.Min(12, repository.HeadCommit.Length)]}  "
                + $"{repository.FileCount:N0} {ToolReply.Plural(repository.FileCount, "file")}, {repository.LineCount:N0} {ToolReply.Plural(repository.LineCount, "line")}\n");

        AppendLanguages(text, overview);
        AppendTree(text, overview);
        AppendLargestFiles(text, overview);
        AppendChurn(text, overview);
        AppendAuthors(text, overview);
        return ToolReply.Cap(text.ToString(), "Use list_tree, hot_files or git_log for the section you want in full.");
    }

    private static void AppendLanguages(StringBuilder text, IndexOverview overview)
    {
        // An agent reading ".vh" beside "C#" has to be able to tell that the first is an extension
        // nobody mapped and not a language this server recognised. The row carries a marker and the
        // heading says once what it means, rather than the same sentence on every unmapped row.
        text.Append(overview.Languages.Any(language => !language.Mapped)
            ? "\nLanguages (* an extension no language profile covers)\n"
            : "\nLanguages\n");
        if (overview.Languages.Count == 0)
        {
            text.Append("  No files are indexed for this project.\n");
            return;
        }

        foreach (var language in overview.Languages)
        {
            string name = language.Mapped ? language.Name : language.Name + " *";
            text.Append(CultureInfo.InvariantCulture,
                $"  {name,-14}{language.Files,7:N0} {ToolReply.Plural(language.Files, "file"),-6}{language.Lines,9:N0} {ToolReply.Plural(language.Lines, "line"),-6}");
            if (language.Skipped > 0)
                text.Append(CultureInfo.InvariantCulture,
                    $"  ({language.Skipped:N0} not indexed: binary or oversized)");
            text.Append('\n');
        }

        if (overview.OtherLanguages > 0)
            text.Append(CultureInfo.InvariantCulture,
                $"  and {overview.OtherLanguages} further {ToolReply.Plural(overview.OtherLanguages, "language or extension", "languages or extensions")}.\n");
    }

    private static void AppendTree(StringBuilder text, IndexOverview overview)
    {
        if (overview.Tree.Count == 0) return;

        // Counts first and the path last, the way every other section here reads: a path is the one
        // field with no bound on its length, so anything after it is a column that does not line up.
        text.Append("\nTop level\n");
        foreach (var root in overview.Tree)
        {
            foreach (var folder in root.Folders)
                TreeRow(text, folder.Files, folder.Lines, folder.SizeBytes, folder.QualifiedPath + "/");
            // A repository with no root files says nothing: "0 files at the root" is a row about nothing.
            if (root.RootFiles > 0)
                TreeRow(text, root.RootFiles, root.RootLines, root.RootBytes,
                    root.QualifiedPath.Length == 0 ? "at the root" : $"at the root of {root.QualifiedPath}");
        }

        if (overview.OtherFolders > 0)
            text.Append(CultureInfo.InvariantCulture,
                $"  and {overview.OtherFolders} further top-level {ToolReply.Plural(overview.OtherFolders, "folder")}; list_tree shows them all.\n");
    }

    /// <summary>
    ///     One row of the top level. A root-file count is drawn in a folder's columns, so the counts of
    ///     the two line up and the label is what tells them apart.
    /// </summary>
    private static void TreeRow(StringBuilder text, int files, long lines, long bytes, string label) =>
        text.Append(CultureInfo.InvariantCulture,
            $"  {files,6:N0} {ToolReply.Plural(files, "file"),-6}{lines,9:N0} {ToolReply.Plural(lines, "line"),-6}{ToolReply.Bytes(bytes),10}  {label}\n");

    private static void AppendLargestFiles(StringBuilder text, IndexOverview overview)
    {
        if (overview.LargestFiles.Count == 0) return;

        text.Append("\nLargest files\n");
        foreach (var file in overview.LargestFiles)
            text.Append(CultureInfo.InvariantCulture,
                $"  {ToolReply.Bytes(file.SizeBytes),10}  {file.LineCount,8:N0} {ToolReply.Plural(file.LineCount, "line"),-6}  {file.QualifiedPath}\n");
    }

    private static void AppendChurn(StringBuilder text, IndexOverview overview)
    {
        var churn = overview.Churn;
        if (churn.Window() is not { } window)
        {
            text.Append("\nMost changed\n  ").Append(ToolReply.NoHistory).Append('\n');
            return;
        }

        text.Append(CultureInfo.InvariantCulture, $"\nMost changed, {window.Describe()}\n");
        if (churn.Files.Count == 0)
        {
            text.Append(
                "  No commit in that window changed a file. Use hot_files with more days to look further back.\n");
            return;
        }

        // Drawn by the same helper hot_files draws its ranking with, because the two rank the same
        // files over the same window and a reader compares them line for line.
        foreach (var file in churn.Files) ToolReply.ChurnRow(text, "  ", file);
    }

    private static void AppendAuthors(StringBuilder text, IndexOverview overview)
    {
        text.Append("\nMost commits, over the whole imported history (who to ask, never who wrote it)\n");
        if (overview.Authors.Count == 0)
        {
            // Said and not left out, for the reason the ranking above says it: a section that simply
            // vanishes reads as a project nobody has worked on, which is the opposite claim.
            text.Append("  ").Append(ToolReply.NoHistory).Append('\n');
            return;
        }

        // The row the authors tool and a filtered git_log draw, so an address read here is the same
        // text an agent types into that filter (ToolReply.AuthorRow).
        foreach (var author in overview.Authors)
            ToolReply.AuthorRow(text, "  ",
                new RecordedAuthor(author.Name, author.Email, author.Commits, author.LastCommit));
    }
}
