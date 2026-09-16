# History lives in the project index, and clones become full and permanent

A project's index gains its repositories' history: `commits`, `commit_files` and `attribution` in
the same DuckDB file as `files` and `lines`, built from the same local copies by the same refresh.
Agents could not answer "who worked on this validation" because nothing was ever fetched to answer
it from — clones were `Depth = 1` (ADR-0003), so the history did not exist on disk to be skipped.
The alternative was a second store with its own lifecycle, which history's shape argues for: it is
append-only, and everything else here is rebuilt.

One file per project wins anyway, because it is what every invariant in ADR-0003 is built on. One
attach, one durable copy, one delete, one restore, one free-space figure. A second store would
double all five, and an index that had a code half and a history half at different commits is a
class of bug that cannot occur while they are written by the same build.

## What is stored

- **`commits`**: `commit_id`, `sha`, author name, email and date, subject, body. The author only,
  never the committer: a rebase makes them disagree, and the author is who wrote the code.
- **`commit_files`**: which paths each commit touched, with the change kind and line counts. This
  is what `files.first_commit` and `files.last_commit` fall out of, at no extra cost in the walk.
- **`attribution`**: line ranges, keyed by `blob_sha` rather than by `file_id`.
- **`lines.commit_id`**: the attribution materialized per line during the build, so the read path
  never joins — the same reasoning ADR-0003 gives for `files.qualified_path`.

Per-line attribution is a column and not a table. Blame is run-structured by construction, so on a
table already sorted by file it is long runs of one value and DuckDB's RLE leaves almost nothing;
a second table would repeat `file_id` and `line_number` for every line of the project.

## What is not stored

Diffs. Answering "when was this bug introduced" honestly needs the added and removed lines of every
commit, which is larger than the entire current index and needs blobs fetched on demand — and
blobless clone is `--filter=blob:none`, which libgit2 does not implement, so it needs the git binary
this image deliberately does not install (ADR-0003: a token never reaches process arguments).
Attribution answers the ownership question and is honest about the other one; the tools say so.

## Consequences

- **`commit_id` is allocated once and never renumbered.** `lines` is rebuilt from the clone on every
  refresh while `commits` is carried over, so a walk that re-sequenced commit ids the way `file_id`
  is sequenced would repoint every carried-over row at a different commit. Silently, and with
  nothing about the index looking broken. `HistoryTests` rebuilds twice and asserts a known
  line still attributes to the same SHA; that test is the invariant. `commits.repo_slug` is there for
  the same reason: a `repo_id` is a position in the build's list and moves when a repository is added
  or removed, which would repoint every carried-over commit at a different repository.
- **Keying attribution by `blob_sha` is what makes it affordable.** An unchanged blob has identical
  content and therefore identical attribution, so the shadow build carries its ranges over instead
  of blaming it again — and a file that only moved keeps them, which buys rename-following for the
  half that matters without similarity detection on every commit of the walk. `CommittedFile` gains
  it as a plain string, so the seam `ModuleBoundaryTests` holds shut — that no LibGit2Sharp type is
  named outside `Git/` — is untouched.
- **Clones are now full and permanent.** Removing `Depth = 1` makes a clone its repository's whole
  object store, and deleting it after a build would re-download that on every refresh. ADR-0003 left
  the question open; full history closes it. Clones are now the largest thing on the 8 GiB ephemeral
  disk, and the free-space gate is the only guard there is: it must also account for a first refresh
  of a new repository, which downloads an unbounded amount before anything is indexed.
- **History fills in the shadow, before the swap, like everything else.** It was going to be a
  background job running after the swap, so that a new project was searchable before its first blame
  finished. That is a write into a live index, which CODING_STANDARDS forbids outright, and buying
  it would have cost either a carve-out in that rule or a second full write of the index. Neither is
  worth what it buys: the cost it avoids is a first build only, and a first build is the one nobody
  is waiting on yet. So a project with a long history is unavailable until its first build finishes,
  and that is the accepted price. If it ever stops being acceptable, the lever is a bound on how much
  a single build may blame — not a live write.
- **`SchemaVersion` goes to 3 with no migration, and the first warm-up after this deploys is long.**
  Every project's durable copy is discarded and rebuilt from git, and that rebuild is now a full
  clone of every repository in it.
