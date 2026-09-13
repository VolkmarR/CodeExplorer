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
  project's index, the shadow file during a rebuild, the working copies and DuckDB's spill files.
  Working copies are therefore shallow clones (`--depth 1 --single-branch`). If the real project mix
  approaches ten SrcRadix-sized codebases (649 MB of index each), this is the constraint that ends
  the Consumption plan.
- **Microsoft documents nothing about exceeding that quota** — not eviction, not restart, not
  `ENOSPC`. The free-space guard before a rebuild exists because avoiding the condition is the only
  available strategy.
- One repository changing rebuilds its whole project. At 32.7 s for a full SrcRadix import against
  scheduled off-peak refreshes this is affordable; if a project ever grows to several such
  repositories, the escape hatch is per-repository shadow tables rather than per-repository files.
- No transaction can write to two projects at once — DuckDB forbids it, and nothing here needs it.
- Two assumptions need a spike before real code, because a failure on either changes the layout:
  whether one connection's `ATTACH` is visible to another in DuckDB.NET (the instance cache decides
  whether N files mean one instance or N), and whether `match_bm25` works against an attached
  database under `USE` on DuckDB 1.5.x. If the second fails, the fallback is a connection per file,
  which reopens the N-memory-limits problem.
