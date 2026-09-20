namespace CodeExplorer;

/// <summary>
///     The tables a project index holds, and the version stamped into every one of them. They sit
///     apart from <see cref="ProjectIndexes" />'s attach, pool and swap machinery because they are
///     read by a different reader: someone asking what a column means opens this, and someone asking
///     how a connection is bound never needs to scroll past it. The two belong together — the version
///     is bumped exactly when the DDL below changes shape — and to nothing else in that file.
/// </summary>
public sealed partial class ProjectIndexes
{
    /// <summary>
    ///     Bumped when the tables below change shape, so a durable copy from an older build is rebuilt
    ///     from git instead of restored into a schema it no longer fits (#9).
    /// </summary>
    public const int SchemaVersion = 9;

    /// <summary>
    ///     Paths inside <c>files</c> stay repository-relative and <c>repo_id</c> scopes them; the
    ///     materialised <c>qualified_path</c> and <c>directory</c> keep the read paths join-free
    ///     (ADR-0003). A file that is committed but not indexed (binary, oversized) is still a row with
    ///     a <c>skip_reason</c>, so a tree listing shows it and a search can say why it was excluded.
    ///     <c>lines</c> carries one row per text line; a file's content is reassembled from them rather
    ///     than stored twice, which halves the file next to the proof of concept's layout.
    ///     The last three tables are the history ADR-0007 put in the same file as the code, so one
    ///     attach, one durable copy and one delete cover both and neither can be at a different commit
    ///     than the other.
    /// </summary>
    private const string Schema = """
                                  CREATE TABLE index_info (
                                      schema_version    INTEGER NOT NULL,
                                      built_at          TIMESTAMPTZ NOT NULL,
                                      fts_indexed       BOOLEAN NOT NULL,
                                      -- How this index named its files (ADR-0006). It is recorded here rather
                                      -- than read from the control database, because a qualified path cannot be
                                      -- parsed without it and Search answers from the index alone (ADR-0005).
                                      single_repository BOOLEAN NOT NULL);
                                  CREATE TABLE repositories (
                                      repo_id     INTEGER PRIMARY KEY,
                                      slug        VARCHAR NOT NULL UNIQUE,
                                      url         VARCHAR NOT NULL,
                                      head_commit VARCHAR NOT NULL,
                                      file_count  INTEGER NOT NULL,
                                      line_count  BIGINT NOT NULL,
                                      -- What the tree at this repository weighs, skipped files
                                      -- included. Recorded beside the other two counts rather than
                                      -- summed over `files` per call: the root of a list_tree asked
                                      -- for it on every call and was the only reason that read
                                      -- joined the largest table in the index at all (#149).
                                      byte_count  BIGINT NOT NULL);
                                  CREATE TABLE files (
                                      file_id        BIGINT PRIMARY KEY,
                                      repo_id        INTEGER NOT NULL,
                                      path           VARCHAR NOT NULL,
                                      qualified_path VARCHAR NOT NULL,
                                      directory      VARCHAR NOT NULL,
                                      name           VARCHAR NOT NULL,
                                      extension      VARCHAR NOT NULL,
                                      size_bytes     BIGINT NOT NULL,
                                      line_count     INTEGER NOT NULL,
                                      skip_reason    VARCHAR,
                                      -- The commits this file was first and last changed by, from the
                                      -- walk rather than from a blame, so they cost nothing beyond it.
                                      -- Null where history was not imported for the repository, which
                                      -- is a different answer from "never changed" and must stay so.
                                      first_commit   INTEGER,
                                      last_commit    INTEGER,
                                      -- What this file declares itself to be: a C# namespace, a
                                      -- Delphi unit. It is the other end of an import edge — a name
                                      -- with nothing to resolve against resolves to nothing — and it
                                      -- is a column here rather than a table because it is one value
                                      -- per file and every read of it is already reading this row.
                                      -- Null where the file declares none, which is most languages.
                                      module         VARCHAR);
                                  CREATE TABLE lines (
                                      line_id     BIGINT PRIMARY KEY,
                                      file_id     BIGINT NOT NULL,
                                      line_number INTEGER NOT NULL,
                                      content     VARCHAR NOT NULL,
                                      -- Attribution materialised per line so the read path never joins,
                                      -- the same reasoning files.qualified_path follows (ADR-0003). A
                                      -- column and not a table: blame is runs of one value on a table
                                      -- already sorted by file, which RLE leaves almost nothing of,
                                      -- where a table would repeat file_id and line_number per line.
                                      commit_id   INTEGER);
                                  CREATE TABLE commits (
                                      -- Allocated once and never renumbered. lines is rebuilt from the
                                      -- clone on every refresh while this table is carried over, so a
                                      -- walk that re-sequenced these would silently repoint every
                                      -- carried-over row at a different commit (ADR-0007).
                                      commit_id    INTEGER PRIMARY KEY,
                                      -- The repository's slug and not its repo_id, for the same reason:
                                      -- a repo_id is the position of the repository in the build's list
                                      -- and moves when one is added or removed, which would repoint
                                      -- every carried-over commit at a different repository. A slug is
                                      -- stable by definition (CONTEXT.md, Repository Slug).
                                      repo_slug    VARCHAR NOT NULL,
                                      sha          VARCHAR NOT NULL,
                                      -- The author and never the committer: a rebase makes the two
                                      -- disagree, and the author is who wrote the code.
                                      author_name  VARCHAR NOT NULL,
                                      author_email VARCHAR NOT NULL,
                                      authored_at  TIMESTAMPTZ NOT NULL,
                                      subject      VARCHAR NOT NULL,
                                      body         VARCHAR NOT NULL);
                                  CREATE TABLE commit_files (
                                      commit_id   INTEGER NOT NULL,
                                      -- Repository-relative, like files.path, and deliberately not a
                                      -- file_id: a commit names paths that no longer exist at HEAD and
                                      -- therefore have no row in files at all.
                                      path        VARCHAR NOT NULL,
                                      change_kind VARCHAR NOT NULL,
                                      added       INTEGER NOT NULL,
                                      deleted     INTEGER NOT NULL,
                                      -- Where a renamed or copied change moved the content from, and
                                      -- NULL for every other kind. libgit2's rename detection is on by
                                      -- default and the walk already reads this to carry attribution
                                      -- across a move; writing it down is what lets a query see that
                                      -- two paths were once one thing (#131). Nullable and not a
                                      -- sentinel: "this change moved nothing" is an absence, and a
                                      -- path equal to `path` is what a non-rename used to look like.
                                      old_path    VARCHAR);
                                  CREATE TABLE attribution (
                                      -- The attribution of every text file at the newest recorded
                                      -- commit, as runs. It is the state the next build replays new
                                      -- commits onto, which is why it is carried over and why it is
                                      -- keyed by slug and path rather than file_id: a file_id is a
                                      -- position in the walk and does not survive a rebuild (ADR-0007).
                                      repo_slug  VARCHAR NOT NULL,
                                      path       VARCHAR NOT NULL,
                                      start_line INTEGER NOT NULL,
                                      end_line   INTEGER NOT NULL,
                                      commit_id  INTEGER NOT NULL);
                                  CREATE TABLE path_lineage (
                                      -- What every path was called before, one row per hop of the
                                      -- chain, computed by the build that recorded the renames (#148).
                                      -- It is a property of the index and not of a request: the walk
                                      -- that discovered it per call ran two scans of commit_files per
                                      -- hop and one combined count at the end, up to seventeen scans
                                      -- of the largest history table for one scoped answer.
                                      -- Keyed by slug and path like attribution, and for the same
                                      -- reason: a repo_id is a position in the build's list.
                                      repo_slug        VARCHAR NOT NULL,
                                      -- The scope a caller named. Every path a rename leads back from
                                      -- has rows here, files and directories alike, because either
                                      -- can be a scope.
                                      path             VARCHAR NOT NULL,
                                      -- 1 is the immediately previous name, 2 the one before that.
                                      -- Stored rather than recomputed from previous_path at read
                                      -- time, so the rename ring and the hop cap the walk guards
                                      -- against are settled once, here, and a read is one ordered
                                      -- select with no recursion.
                                      hop              INTEGER NOT NULL,
                                      previous_path    VARCHAR NOT NULL,
                                      -- Commits recorded at or under previous_path: the number the
                                      -- scope's own count is missing.
                                      previous_commits INTEGER NOT NULL,
                                      -- Distinct commits accounted for by the scope and its whole
                                      -- chain, the same on every row of a scope. Distinct because the
                                      -- rename commit itself touches both sides and would otherwise
                                      -- inflate the very number the note exists to get right, and
                                      -- stored per scope rather than summed from the hops for that
                                      -- reason.
                                      combined_commits INTEGER NOT NULL);
                                  CREATE TABLE imports (
                                      -- One row per name one file imports (#55), read from the line
                                      -- walk the build already performs. The name is kept as it was
                                      -- written, whatever it resolved to: an edge reported only when
                                      -- it resolves would tell a reader the file depends on nothing
                                      -- when it depends on something this could not place.
                                      import_id   BIGINT PRIMARY KEY,
                                      file_id     BIGINT NOT NULL,
                                      line_number INTEGER NOT NULL,
                                      name        VARCHAR NOT NULL,
                                      -- What kind of name it is, and so how it was resolved: a module
                                      -- against what a file declares itself to be, a path against the
                                      -- importing file's own directory.
                                      shape       VARCHAR NOT NULL,
                                      -- The file it turned out to name, or null. A resolved edge is
                                      -- what makes the reverse direction answerable at all, which is
                                      -- the half of this that cannot be got by reading the file.
                                      target_file BIGINT,
                                      -- Why it names no file here, or null where it does. The two
                                      -- are exclusive and both are filled by the resolution pass, so
                                      -- a row with neither is a row that pass never reached.
                                      unresolved  VARCHAR,
                                      -- How the line was read: from its text, or by a parser for the
                                      -- language (ADR-0008). Recorded rather than assumed, so that
                                      -- the day a parser-backed analyser is registered the reply
                                      -- stops understating what it knows without a schema change.
                                      evidence    VARCHAR NOT NULL);
                                  CREATE TABLE project_overview (
                                      -- Exactly one row, written by the build that produced the index
                                      -- (#51), so a caller orienting itself reads a row instead of
                                      -- running five aggregates over the largest tables here.
                                      -- One JSON column rather than a set of LIST(STRUCT) columns: the
                                      -- document is read whole and never queried into, so nested
                                      -- columns would buy a queryability nothing uses and cost every
                                      -- read a nested-value reader. IndexOverview says the same.
                                      document VARCHAR NOT NULL);
                                  """;
}
