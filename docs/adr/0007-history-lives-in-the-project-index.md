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
  It also carries `old_path`: where a `renamed` or `copied` change moved the content from, and NULL
  for every other kind. The diff is produced with libgit2's rename detection at its defaults, so the
  walk already receives that path and already consumes it in memory to carry attribution across a
  move; it was discarded at append time until there was a column for it. Writing it down is what
  lets a query see that two paths were once one thing, which is how a path-scoped answer knows to
  say what its scope was called before (#131). NULL and not a copy of `path`: "moved from nowhere"
  is an absence, and storing the path itself would make every ordinary row look like a rename onto
  itself.
- **`attribution`**: line ranges per repository and path — the attribution of every text file as of
  the newest recorded commit, which is the state the next build replays new commits onto. Keyed by
  slug and path rather than by `file_id`, which is a position in the walk and does not survive a
  rebuild.
- **`lines.commit_id`**: the attribution materialized per line during the build, so the read path
  never joins — the same reasoning ADR-0003 gives for `files.qualified_path`.
- **`path_lineage`**: what every path was called before, one row per hop of the chain, derived from
  `commit_files.old_path` by the same build (#148). It is derived rather than walked, so a refresh
  recomputes it instead of inheriting it, and it is not among the tables a new shadow carries over.
  It is still in the durable copy, which is a different mechanism: a restore has no build to
  recompute it, and an index restored without it would stop saying what a scope was called before —
  silently, because a missing chain and a path nobody renamed read the same.

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
  or removed, which would repoint every carried-over commit at a different repository. The same
  reasoning decides whose history a build prunes. It prunes the repositories no longer configured,
  not the ones that failed to open: a fetch that fails once skips the repository's files for that
  refresh and keeps its history as it was. Otherwise the next refresh would re-walk it from the root
  under new ids (#228).
- **Attribution is replayed from the walk, not blamed per file.** The first version blamed every
  file at HEAD once, keyed by blob hash so an unchanged file was not blamed again. Measured on a
  real repository of 4,300 commits and 8,100 files, that was 1.4 s a file and between ninety minutes
  and three hours for a first build, with libgit2 walking the same history once per file. The walk
  already renders a patch per commit for `commit_files`; reading each hunk's positions out of that
  text and applying them, oldest commit first, to a per-file array of "which commit wrote this line"
  gives the attribution at HEAD in one pass — 32 s for the same repository, 0.4 s of which is the
  replay itself, agreeing with blame on every line of a random sample. The costs it moves: a refresh
  loads the carried-over ranges and replays only the new commits, so `attribution` is state and not a
  cache; a history rewritten upstream is re-imported from the root, which a walk that does not stop
  at the newest recorded commit detects, and which deletes the repository's commits and allocates
  them fresh ids above every remaining one — no id is renumbered, the rows that held it are gone
  (#227); and renames follow
  libgit2's detection on the patch, where blob keying followed content equality. Parallel blame was
  measured and rejected first — slower at two and four workers on a two-vCPU host.
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
  and that is the accepted price. With attribution replayed rather than blamed, that price is the
  clone and the commit walk, not hours.
- **`SchemaVersion` goes to 3 with no migration, and the first warm-up after this deploys is long.**
  Every project's durable copy is discarded and rebuilt from git, and that rebuild is now a full
  clone of every repository in it. Version 4 followed when attribution moved from blob keying to
  slug-and-path keying; the same rule, a rebuild and no migration, and by then a rebuild was cheap.
- **A path-scoped history call used to scan `commit_files` up to seventeen times, and the build now
  pays that once instead.** Resolving a scope's previous path is a `GROUP BY` over `commit_files`
  joined to `commits`, and there is no index on `path`; walked hop by hop, that was two scans per
  hop and a combined count at the end, per call, for a chain that cannot change between calls. An
  opt-in `follow` argument would have cost nothing and helped only the agent that already suspects a
  rename, which is not the agent that needs help: the measured failure was an agent given 9 commits
  for a directory with 1,252 and no reason to look further. So the answer was the one this entry
  named rather than the argument — a table of rename chains built once by the walk (`path_lineage`,
  #148). A scoped call reads one row per hop from it, and a project that renamed nothing reads none.
- **A change to a history table now costs a full re-walk per project, and that is the price of
  carrying them over.** These three tables are the only ones a refresh inherits rather than rebuilds,
  and they are inherited column for column from whichever copy is at hand — the durable one, or the
  live index file on disk. So a history table that gains a column cannot be inherited by a build that
  did not write it: the insert would be short, and where the shapes happened to line up the carried
  rows would be blind to whatever the new column records, which is a quiet wrong answer rather than a
  failure. Both copies are therefore refused on the same `SchemaVersion` test, and the next refresh
  walks every commit again. Version 7, which added `commit_files.old_path`, was the first to pay it,
  version 8, which added `path_lineage`, pays it again for a table it does not even carry, and
  version 9 pays it for `repositories.byte_count`, which is not a history table at all — the
  test is on the whole schema, because a version meaning "the history tables in particular" would be
  a second version number to keep honest. The walk it costs is the one ADR-0007 measured at 32 s for
  a 4,300-commit repository, once per project, and it is user-visible on the first refresh after a
  deploy.

## Revisited on 2026-09-27: shallow copies in a clone directory that survives

"Clones are now full and permanent" assumed every clone would be made again after this deployed,
which holds on Container Apps, whose disk is wiped when a replica stops, and the `SchemaVersion`
bullet counted that as the cost of a full clone of every repository. A clone directory that survives
a restart — IIS, a developer's machine — held the shallow clones made before this decision, and
nothing replaced them: a fetch never deepens a shallow clone, and the history walk took each
boundary commit for a root that added every file, attributing to it every line older than the copy.

A shallow local copy is now cloned over on its next refresh, one full download each, and the history
recorded from it is recognised by its oldest commit having a parent in the new copy, and imported
again from the root the way a rewritten history is (#227).

## Revisited on 2026-09-29: local copies are repacked

"Permanent" had a cost nobody paid down. Every fetch adds a pack and libgit2 has no gc, so a copy
kept between refreshes only fragments. On IIS, where clones are never wiped, a copy of 4,380 commits
had reached 353 packs and 41,344 loose objects in 457 MiB. Repacked into one pack it took 242 MiB,
read every blob at HEAD in 4.0 s instead of 5.7 s, and walked its history with a tree diff per
commit in 36.7 s instead of 47.9 s.

A fetch is now followed by a repack when the copy holds more than `Git:RepackPackThreshold` packs
(50) or `Git:RepackLooseObjectThreshold` loose objects (5000). Either is enough, so neither
threshold switches the repack off; `Git:RepackEnabled` does. A first clone never is, since it
arrives as one pack. The repack uses LibGit2Sharp's `ObjectDatabase.Pack`, never the git CLI, and
runs under the clone's gate before the refresh opens the copy, because Windows refuses to delete a
pack libgit2 has mapped. Every `*.pack` counts, not only libgit2's `pack-*`: that copy also held
four `loose-*` packs from git maintenance.

Which packs are folded follows git's `repack --geometric=2`. Walking down from the pack holding the
most objects, a pack stays while it holds at least twice everything smaller than it, loose objects
included, and the first that does not is folded together with every pack below it and the loose
objects. The first repack of a fragmented copy folds everything: that copy's largest pack held
145,575 objects, less than twice the rest, which overlap. Each later repack folds only what was
fetched since. Five small fetch packs added after the first repack were folded in 0.2 s at a
25 MiB peak, and the 242 MiB pack was left alone. A split that would still leave more packs than
the threshold folds everything instead, or the copy would be due again after the next fetch.

The packbuilder is handed the object ids one by one, read from the folded packs' indexes and the
loose objects' file names. `Pack(options)` enumerates the whole object database instead, which
meets each object once per pack holding it (636,445 index entries for 256,719 objects) and also
takes along the packs that are meant to stay. On that copy, writing the same 242 MiB pack, handing
over the ids cut peak private memory from 1,097 to 616 MiB and the time from 112 s to 76 s. The
end-to-end run took 115 s at 613 MiB, so the timings are noisy. libgit2 spreads the delta search
over every core by default, and one thread took half as long again for the same memory.
`pack.windowMemory` and `pack.deltaCacheSize` saved under 100 MiB more, which is not enough to
write them into every clone's config. The thresholds are high because the first repack is still
over a minute. On Container Apps under scale to zero, where every clone is fresh, it rarely fires
at all.

The order of the swap is the decision. A first attempt deleted the old packs before the new one was
in place, a delete failed half-way, and the copy was left missing objects. So the new pack is written
outside `objects/pack`, moved in (`.pack`, then the `.idx` that makes it visible), and checked: its
header and index must count what the builder wrote, which must be the number of distinct ids it was
handed, and it must end in the checksum its index records. Only then are the folded packs and the
loose objects deleted, with their read-only attribute cleared first. A failure before the check
leaves the copy as it was. One after it leaves a whole copy with some old files beside the new pack,
which the next repack folds. Nothing a repack does fails the refresh. It is skipped while the disk
has less free space than what it folds occupies plus `Refresh:MinimumFreeBytes`, measured on the
filesystem that holds the copy, so the free-space gate does not reserve room for
it: the repack borrows that room for a moment and returns more than it borrowed.
