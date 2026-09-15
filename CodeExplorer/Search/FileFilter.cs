using System.Text;
using DuckDB.NET.Data;

namespace CodeExplorer;

/// <summary>
///     Which files an index search is allowed to look at, and the SQL that says so. Every search in
///     <c>Search/</c> takes the same four filters, and they are written once here rather than once per
///     service: three services spelling "skip generated files" three ways would answer the same
///     <c>exclude="*.g.cs"</c> differently, which is the kind of drift an agent cannot see.
///     <see cref="Repository" /> is a slug already resolved against the index by
///     <see cref="IndexReader.OpenAsync" />; an unresolved one never reaches the SQL, because an
///     unknown repository must be explained rather than answered with an empty result.
/// </summary>
public sealed record FileFilter(
    string? Repository = null,
    string? Path = null,
    string? Exclude = null,
    string? Extension = null)
{
    /// <summary>Whether anything here could hide a match, which is what an empty answer has to be told apart from.</summary>
    public bool Any =>
        !string.IsNullOrWhiteSpace(Repository) || !string.IsNullOrWhiteSpace(Path) ||
        !string.IsNullOrWhiteSpace(Exclude) || !string.IsNullOrWhiteSpace(Extension);

    /// <summary>
    ///     The filters as a <c>WHERE</c> tail against the <c>files</c> alias <c>f</c>, starting with
    ///     <c>AND</c> so it appends to a condition the caller already has. Path terms are OR-ed (one
    ///     call over several folders), exclude terms AND-ed, both against the lower-cased qualified
    ///     path. A term with <c>*</c> or <c>?</c> is a SQL <c>GLOB</c> over the whole path, where
    ///     <c>*</c> crosses <c>/</c>; anything else is a plain substring.
    /// </summary>
    /// <param name="parameters">Every value is bound, never inlined; the caller passes the list to the command.</param>
    internal string Sql(List<DuckDBParameter> parameters)
    {
        var where = new StringBuilder();

        if (!string.IsNullOrWhiteSpace(Repository))
        {
            // A subquery rather than a join to repositories: the searches this appends to select from
            // lines and files alone, and one more table in their FROM would change every plan for a
            // filter that matches one id.
            where.Append(" AND f.repo_id = (SELECT repo_id FROM repositories WHERE slug = $repo)");
            parameters.Add(new DuckDBParameter("repo", Repository));
        }

        var includes = SplitTerms(Path);
        if (includes.Count > 0)
        {
            where.Append(" AND (");
            for (int i = 0; i < includes.Count; i++)
            {
                if (i > 0) where.Append(" OR ");
                where.Append(Match(includes[i], $"$p{i}"));
                parameters.Add(new DuckDBParameter($"p{i}", includes[i]));
            }

            where.Append(')');
        }

        var excludes = SplitTerms(Exclude);
        for (int i = 0; i < excludes.Count; i++)
        {
            where.Append(" AND NOT (").Append(Match(excludes[i], $"$x{i}")).Append(')');
            parameters.Add(new DuckDBParameter($"x{i}", excludes[i]));
        }

        if (!string.IsNullOrWhiteSpace(Extension))
        {
            where.Append(" AND f.extension = $ext");
            parameters.Add(new DuckDBParameter("ext", Extension.Trim().TrimStart('.').ToLowerInvariant()));
        }

        return where.ToString();

        static string Match(string term, string parameter)
        {
            return term.Contains('*') || term.Contains('?')
                ? $"lower(f.qualified_path) GLOB {parameter}"
                : $"contains(lower(f.qualified_path), {parameter})";
        }
    }

    private static List<string> SplitTerms(string? terms) =>
    [
        .. (terms ?? "")
        .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(t => t.Replace('\\', '/').ToLowerInvariant())
    ];
}
