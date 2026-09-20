using DuckDB.NET.Data;

namespace CodeExplorer;

/// <summary>
///     Fills <c>path_lineage</c>: what every path of every repository was called before, one row per
///     hop of the chain (#148).
///     It used to be discovered per request. A scoped <c>git_log</c>, <c>authors</c>,
///     <c>file_history</c>, <c>co_changed</c> or <c>hot_files</c> asked for the dominant earlier
///     prefix of its scope, then of that prefix, and so on — two scans of <c>commit_files</c> per hop
///     and one combined count at the end, up to seventeen scans of the largest history table to
///     answer one call. Lineage is a property of the index and not of the request: the same walk over
///     the same rows gives the same chain however often it is asked for, so it is walked once, here,
///     and a read is one ordered select.
///     The rule this encodes is unchanged and is the one the read side used to carry: an earlier
///     prefix is the scope's <i>previous path</i> when <b>more</b> than <see cref="Share" /> of the
///     paths the scope has ever recorded came from it. What the comments below explain is how that
///     rule is evaluated for every path at once rather than for one path per query.
/// </summary>
internal static class PathLineageBuilder
{
    /// <summary>
    ///     Hops the chain walk takes before it gives up, whatever it has found. A repository whose
    ///     paths were renamed in a ring — which a rename edge permits and git does not forbid — would
    ///     otherwise walk forever. Visited paths are tracked as well; this is the second guard, and the
    ///     one that bounds the length of an honestly long chain. Both are settled here rather than at
    ///     read time, so that a reader never has to reason about either.
    /// </summary>
    private const int MaxHops = 8;

    /// <summary>
    ///     What makes an earlier prefix the scope's <i>previous path</i> rather than somewhere two files
    ///     happened to move in from: <b>more</b> than this share of the paths the scope has ever
    ///     recorded came from it — a majority. Written down rather than tuned, because the two cases it
    ///     separates are far apart: a directory rename moves nearly everything under it at once
    ///     (measured: 479 of 511 paths in one commit), and a stray file moved in over the years is a
    ///     handful out of hundreds.
    ///     A majority and not "at least half", which are the same bar everywhere except the small scopes
    ///     where the difference decides: a folder of two files that took one in from elsewhere would
    ///     clear "at least half" and be told it used to be elsewhere. A majority also guarantees that
    ///     only one candidate can qualify, so the ordering the pick falls back on decides nothing.
    ///     A string and not a <see cref="double" />, because it is interpolated into SQL and a culture
    ///     that spells the point as a comma would write a syntax error rather than a wrong number.
    /// </summary>
    private const string Share = "0.5";

    /// <summary>
    ///     Walks every chain and writes it. Runs after <c>commit_files</c> is complete, because that is
    ///     the table it reads; the shadow's <c>path_lineage</c> is empty until this, since the table is
    ///     derived and so is not among the ones a new shadow carries over from the live index.
    /// </summary>
    /// <param name="connection">The shadow's connection, already bound to it.</param>
    /// <param name="cancellationToken">Checked before each statement; these are the slow ones.</param>
    public static void Fill(DuckDBConnection connection, CancellationToken cancellationToken)
    {
        try
        {
            Prefixes(connection, cancellationToken);
            Hops(connection, cancellationToken);
            Chains(connection, cancellationToken);
            Counts(connection, cancellationToken);
            Write(connection, cancellationToken);
        }
        finally
        {
            // In a finally for the reason the attribution scratch is: a cancelled or failed build must
            // not leave these on a connection the pool hands out again.
            foreach (string scratch in new[]
                         { "lineage_prefixes", "lineage_hops", "lineage_chain", "lineage_member_commit" })
                connection.Execute($"DROP TABLE IF EXISTS {scratch}", CancellationToken.None);
        }
    }

    /// <summary>
    ///     Every path a commit recorded, against each directory prefix it sits under — <c>src/Model</c>
    ///     and <c>src</c> for <c>src/Model/Contact.cs</c>, and the file's own path last.
    ///     It is what turns "at or under this scope" from a <c>starts_with</c> comparison against every
    ///     row into an equality join, which is the whole reason the per-path queries this replaces were
    ///     expensive. Built from the distinct paths and not from the rows, so its size is the tree and
    ///     not the history.
    /// </summary>
    private static void Prefixes(DuckDBConnection connection, CancellationToken cancellationToken) =>
        connection.Execute(
            """
            CREATE OR REPLACE TEMP TABLE lineage_prefixes AS
            SELECT repo_slug, path, array_to_string(parts[1:depth], '/') AS prefix
            FROM (SELECT repo_slug, path, parts, unnest(generate_series(1, len(parts))) AS depth
                  FROM (SELECT DISTINCT c.repo_slug, cf.path, string_split(cf.path, '/') AS parts
                        FROM commit_files cf JOIN commits c USING (commit_id)))
            """, cancellationToken);

    /// <summary>
    ///     The one path each scope was renamed from, for every scope at once — the query the read side
    ///     used to run once per hop with the scope bound.
    ///     Candidates come from file-level rename rows by <b>prefix mapping</b>: a renamed row differs
    ///     from its old path by a leading prefix alone when the two end in the same segments, and that
    ///     pair of prefixes is a candidate for every depth the shared suffix allows. One row renaming
    ///     <c>model/Contact.cs</c> to <c>src/Model/Contact.cs</c> therefore offers <c>src/Model</c> ←
    ///     <c>model</c> and the file pair, which is how a directory rename nobody recorded as such is
    ///     found at all.
    ///     Candidates are then weighed against how many distinct paths the scope has ever recorded, not
    ///     against how many rows were renamed, which is the denominator that separates a directory
    ///     rename from two files that moved in: renamed rows alone would make any scope whose only
    ///     rename edges came from one place look like it had been that place.
    ///     A candidate on the scope's own branch is rejected whichever way it points. An ancestor —
    ///     <c>src</c> offered for <c>src/Model</c>, because most of <c>src</c> was moved down into it —
    ///     would report a combined total covering all of <c>src</c>, which is the inflated number this
    ///     feature exists to correct, pointing backwards. A descendant is the mirror: the scope's own
    ///     count already prefix-matches it, so "N across the whole chain" would equal "this path's
    ///     alone" and the note would say nothing while looking like it said something.
    ///     Only <c>renamed</c> edges count. A <c>copied</c> one is stored (ADR-0007) and is not a
    ///     previous path: both sides still exist, so "this scope was renamed" would be false and the
    ///     combined total would sum two live paths.
    /// </summary>
    private static void Hops(DuckDBConnection connection, CancellationToken cancellationToken) =>
        connection.Execute(
            $"""
             CREATE OR REPLACE TEMP TABLE lineage_hops AS
             WITH renamed AS (
                 SELECT c.repo_slug, cf.path, cf.commit_id,
                        string_split(cf.path, '/') AS parts,
                        string_split(cf.old_path, '/') AS old_parts
                 FROM commit_files cf JOIN commits c USING (commit_id)
                 -- renamed only, and old_path is null for every kind that moved nothing.
                 WHERE cf.old_path IS NOT NULL AND cf.change_kind = 'renamed'),
             -- `back` is how many segments to drop off both ends: 0 is the file pair itself, 1 the
             -- directory holding it, and so on up to the top-level directory. A pair only maps at a
             -- depth where the dropped segments agree, because an old path that does not end in the
             -- same remainder moved sideways rather than wholesale.
             candidates AS (
                 SELECT repo_slug, path, commit_id,
                        array_to_string(parts[1:len(parts) - back], '/') AS scope,
                        array_to_string(old_parts[1:len(old_parts) - back], '/') AS previous
                 FROM (SELECT *, unnest(generate_series(0, len(parts) - 1)) AS back FROM renamed)
                 WHERE len(old_parts) - back >= 1
                   AND parts[len(parts) - back + 1:] = old_parts[len(old_parts) - back + 1:]),
             -- Every path the scope has ever recorded, read off the prefix table. Only the scopes a
             -- candidate named are counted; the rest of the tree has no bar to clear.
             recorded AS (
                 SELECT p.repo_slug, p.prefix, count(DISTINCT p.path) AS paths
                 FROM lineage_prefixes p
                 JOIN (SELECT DISTINCT repo_slug, scope FROM candidates) s
                     ON s.repo_slug = p.repo_slug AND s.scope = p.prefix
                 GROUP BY p.repo_slug, p.prefix)
             SELECT repo_slug, scope, previous FROM (
                 SELECT ca.repo_slug, ca.scope, ca.previous,
                        -- Share first, then the newest rename, then the path itself: a file scope has
                        -- a denominator of one, so several candidates can qualify and the answer must
                        -- not depend on the order rows came back in.
                        row_number() OVER (PARTITION BY ca.repo_slug, ca.scope
                            ORDER BY count(DISTINCT ca.path) DESC, max(ca.commit_id) DESC, ca.previous)
                            AS pick
                 FROM candidates ca
                 JOIN recorded r ON r.repo_slug = ca.repo_slug AND r.prefix = ca.scope
                 WHERE ca.previous <> '' AND ca.previous <> ca.scope
                   -- Never the scope's own branch, either way up it.
                   AND NOT starts_with(ca.previous, ca.scope || '/')
                   AND NOT starts_with(ca.scope, ca.previous || '/')
                 GROUP BY ca.repo_slug, ca.scope, ca.previous, r.paths
                 HAVING count(DISTINCT ca.path) > r.paths * {Share})
             WHERE pick = 1
             """, cancellationToken);

    /// <summary>
    ///     The chains, hop by hop: the previous path of the scope, then of that path, so
    ///     <c>src/Model</c> ← <c>model</c> ← <c>Model</c> comes back as a chain rather than one step.
    ///     The recursive walk carries the paths it has already stood on, so a rename ring stops at the
    ///     first repeat instead of circling, and <see cref="MaxHops" /> stops an honestly long one.
    /// </summary>
    private static void Chains(DuckDBConnection connection, CancellationToken cancellationToken) =>
        connection.Execute(
            $"""
             CREATE OR REPLACE TEMP TABLE lineage_chain AS
             WITH RECURSIVE walk AS (
                 SELECT repo_slug, scope AS root, 1 AS hop, previous AS member,
                        [scope, previous] AS seen
                 FROM lineage_hops
                 UNION ALL
                 SELECT w.repo_slug, w.root, w.hop + 1, h.previous, list_append(w.seen, h.previous)
                 FROM walk w JOIN lineage_hops h
                     ON h.repo_slug = w.repo_slug AND h.scope = w.member
                 WHERE w.hop < {MaxHops} AND NOT list_contains(w.seen, h.previous))
             SELECT repo_slug, root, hop, member FROM walk
             """, cancellationToken);

    /// <summary>
    ///     Which commits each path on a chain accounts for, as one row per path and commit. Written out
    ///     rather than counted in place because the two numbers a reply needs are both taken from it:
    ///     a hop's own commits, and the distinct commits of a whole chain — which is not their sum,
    ///     since the rename commit touches both sides of every hop.
    ///     The roots are in here beside the members: a chain accounts for the scope's own commits too.
    ///     This is where the build now spends the time the reads used to, so the repository is part of
    ///     the join key rather than a predicate applied after it: a path matched across repositories
    ///     first and narrowed afterwards would build an intermediate the size of every repository's
    ///     paths against every other's, in the one statement here that is worth keeping small.
    /// </summary>
    private static void Counts(DuckDBConnection connection, CancellationToken cancellationToken) =>
        connection.Execute(
            """
            CREATE OR REPLACE TEMP TABLE lineage_member_commit AS
            SELECT DISTINCT m.repo_slug, m.member, recorded.commit_id
            FROM (SELECT DISTINCT repo_slug, member FROM lineage_chain
                  UNION SELECT DISTINCT repo_slug, root FROM lineage_chain) m
            JOIN lineage_prefixes p ON p.repo_slug = m.repo_slug AND p.prefix = m.member
            JOIN (SELECT c.repo_slug, cf.path, cf.commit_id
                  FROM commit_files cf JOIN commits c USING (commit_id)) recorded
                ON recorded.repo_slug = m.repo_slug AND recorded.path = p.path
            """, cancellationToken);

    /// <summary>
    ///     One row per hop, with the two counts a reply reads off it. Both counts are left-joined: a
    ///     path renamed in the commit that introduced it leads back to a spelling no commit ever
    ///     recorded under, and a hop with nothing behind it is still a hop — dropping it would lose the
    ///     rename the caller is standing on.
    /// </summary>
    private static void Write(DuckDBConnection connection, CancellationToken cancellationToken) =>
        connection.Execute(
            """
            INSERT INTO path_lineage
            SELECT ch.repo_slug, ch.root, ch.hop, ch.member,
                   coalesce(hop_commits.commits, 0), coalesce(chain_commits.commits, 0)
            FROM lineage_chain ch
            LEFT JOIN (SELECT repo_slug, member, count(*) AS commits
                       FROM lineage_member_commit GROUP BY repo_slug, member) hop_commits
                ON hop_commits.repo_slug = ch.repo_slug AND hop_commits.member = ch.member
            -- The scope and every hop of its chain, counted once each: the rename commit is on both
            -- sides of a hop and would inflate the very number the note exists to get right.
            LEFT JOIN (SELECT w.repo_slug, w.root, count(DISTINCT mc.commit_id) AS commits
                       FROM (SELECT repo_slug, root, member FROM lineage_chain
                             UNION SELECT DISTINCT repo_slug, root, root FROM lineage_chain) w
                       JOIN lineage_member_commit mc
                           ON mc.repo_slug = w.repo_slug AND mc.member = w.member
                       GROUP BY w.repo_slug, w.root) chain_commits
                ON chain_commits.repo_slug = ch.repo_slug AND chain_commits.root = ch.root
            """, cancellationToken);
}
