# One DuckDB file per project, attached to a single instance

Each project's index is its own DuckDB file. All of them are attached to one DuckDB instance, and
every connection runs `USE <project slug>` before querying. The alternative was one shared database
with a `project_id` column on every table.

Separate files give each project its own `lines` table and therefore its own `fts_main_lines`
index, so BM25 relevance is computed against that project's code rather than across every
department's. A shared table would score against the whole corpus and filter afterwards, and
rebuilding one project's index would rebuild everyone's. Separate files also leave the table names
unchanged, so the roughly 35 hardcoded SQL identifiers inherited from the `CodeSearch` proof of
concept keep working; a `project_id` column would add a predicate to all of them.

Attaching to one instance rather than opening one instance per file is what keeps memory
controllable: `memory_limit` has global scope and the buffer manager serves all attached databases
from one pool. Separate instances would each default to 80% of detected RAM — which inside a
container is usually the host's RAM, not the cgroup's. The `USE` before querying is the documented
workaround for `match_bm25` failing to resolve its internal tables in an attached database
(duckdb/duckdb#13523).

## Shape

- **Inside a project file**: `repo_id` on `files`, paths stay repo-relative, plus a materialized
  `qualified_path`. The id scopes glob, tree and extension filters to one repository without
  `LIKE 'repo/%'` scans; the materialized column keeps the read paths join-free.
- **Durability**: Parquet per project under its own blob prefix, making a project independently
  restorable, deletable and backed up. Attach and restore lazily on first connection, with a cron
  warm-up walking every project before work hours — an off-hours wake then costs one project's
  restore (~19 s) rather than all of them (~3.2 min).
- **Refresh**: rebuild the whole project into a shadow file, drain in-flight queries with a hard
  timeout, then swap. One rebuild at a time across the whole server, gated on a free-space check.

## Consequences

- **8 GiB of ephemeral storage is a hard ceiling.** Azure Container Apps allocates it by vCPU, and
  "over 1 vCPU" is the top of the table — 4 vCPU gets the same 8 GiB, storage appears nowhere in the
  quota system, and a Consumption-only environment caps at 2 vCPU / 4 GiB regardless. It holds every
  project's index, the shadow file during a rebuild, the local clones and DuckDB's spill files.
  Clones are therefore shallow and bare. If the real project mix
  approaches ten SrcRadix-sized codebases (649 MB of index each), this is the constraint that ends
  the Consumption plan.
- **Microsoft documents nothing about exceeding that quota** — not eviction, not restart, not
  `ENOSPC`. The free-space guard before a rebuild exists because avoiding the condition is the only
  available strategy.
- One repository changing rebuilds its whole project. At 32.7 s for a full SrcRadix import against
  scheduled off-peak refreshes this is affordable; if a project ever grows to several such
  repositories, the escape hatch is per-repository shadow tables rather than per-repository files.
- No transaction can write to two projects at once — DuckDB forbids it, and nothing here needs it.
- **Both load-bearing assumptions were verified** against DuckDB.NET.Data.Full 1.5.5. Connections
  sharing a connection string share one instance: an `ATTACH` on one is visible to all, and a
  `memory_limit` set on one reads back on the others. `match_bm25` fails qualified
  (`Catalog Error: Table with name "fts_main_lines.terms" does not exist` — duckdb/duckdb#13523 is
  still open on 1.5.5) and succeeds after `USE`. `ATTACH` is instance-wide while `USE` is
  per-connection, which is what lets one instance serve every project without connections treading
  on each other. `PRAGMA create_fts_index` also works inside an attached database, so a shadow
  build needs no special handling.
- **There is no working copy.** Clones are bare, and files are read from the object database: the
  HEAD tree gives the file list and blobs give the content. Measured on Xs2Cs (2,822 files, 1.3 GB
  of history today): a shallow bare clone is 17.1 MB in 3.4 s against 68.5 MB in 7.1 s with a
  working tree, a tree walk lists every file in 45 ms, and 500 blobs read as text in 82 ms. A
  shallow fetch — the refresh path — takes 519 ms and leaves the clone shallow. Walking the tree
  rather than a directory also removes `.gitignore` from the design entirely: the index holds what
  is committed. `RetrieveStatus`, which exists only to answer what is ignored, costs 763–1084 ms
  against the 45 ms tree walk and is never needed.
- **Git is driven through LibGit2Sharp 0.32.0, not the git CLI.** No git binary in the image, typed
  errors instead of parsed stderr, and credentials passed through `CredentialsProvider` so a token
  never appears in process arguments or a URL on disk. Three limits come with it: libgit2 has no
  Git LFS support and would silently index pointer files instead of source, so a repository
  declaring `filter=lfs` must be refused rather than indexed; shallow clone is unsupported over the
  local transport (`shallow fetch is not supported by the local transport`), so tests cannot seed a
  shallow fixture from a path; and checkout can exceed `MAX_PATH` on Windows, which production on
  Linux avoids and bare clones avoid entirely.
- **`DETACH` offers no protection during a swap, so the drain is entirely ours.** It succeeds while
  another connection has `USE`d that database, and returns in 0 ms while a query is actively
  scanning it. The in-flight query completes correctly against its snapshot, but the orphaned
  connection fails on its next statement with `Binder Error: Catalog "<project>" does not exist!` —
  even for `SELECT current_database()`. A connection must therefore issue `USE` per checkout and
  never hold one across a swap.

## Revisited for #37, on 2026-09-15

The bare shallow clone is the refresh's and nobody else's. It was also what `list_tree` answered
from, which made it a second source of truth beside the index and contradicted CONTEXT.md; that tool
now reads the index like every other. The clone is opened in one place, `GitClones.OpenRefreshedAsync`,
which decides empty and LFS once and hands the refresh a `LocalCopy` of plain records, so no
LibGit2Sharp type leaves `Git/` and `ModuleBoundaryTests` keeps it that way. Nothing may depend on the
clone existing between refreshes: it is temporary, and deleting it after a build is a decision this
ADR leaves open, to be taken on the storage figures above rather than on convenience.
