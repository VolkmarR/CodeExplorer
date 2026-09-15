# CodeExplorer

CodeExplorer serves a read-only, searchable view of source code to coding agents over a remote MCP
endpoint. It exists so an agent can explore a codebase that is not on the machine it runs on.

## Language

**Project**:
A named collection of repositories explored together as a single body of code. A project is the
unit an agent connects to, and the unit a search spans.
_Avoid_: Workspace, solution, tenant

**Repository**:
One git remote belonging to exactly one project, which CodeExplorer keeps a local copy of and
refreshes. A project of one repository is the common case, and a project may be declared a
single-repository project to be named accordingly (ADR-0006); it is a repository either way.
_Avoid_: Source, codebase

**Local copy**:
The shallow bare clone of a repository that a refresh brings up to date and reads to build the
index. It exists for that and nothing else: no tool, endpoint or view reads it, it is temporary,
and it may be deleted at any time without anything but the next refresh noticing.
_Avoid_: Clone (as a noun for the thing), checkout, working copy

**Index**:
The queryable representation of a project's code, built from its repositories and rebuilt as they
are refreshed. Every MCP tool answers from the index, never from the local copy directly.
_Avoid_: Database, cache, corpus

**Match**:
A single line of a single file that satisfies a search, with the position that produced it. A file
with twelve matching lines yields twelve matches.
_Avoid_: Hit, result

**Reference**:
A place where an identifier appears in code in a way that looks like real use, rather than in a
comment, a string or an import. Determined from the text alone, never from language analysis, so a
reference is strong evidence and not proof.
_Avoid_: Usage, call site, occurrence

**Qualified Path**:
How every file in a project is named: its repository, then its path within that repository. A
project is therefore one flat namespace, and two repositories may each contain `src/index.ts`
without collision. In a single-repository project it is the path inside that repository alone, with
no leading slug to disambiguate a set of one (ADR-0006) — so a path cannot be read without knowing
which project it belongs to.
_Avoid_: Full path, absolute path

**Single-Repository Project**:
A project declared at creation to hold one repository and to name its files without a repository
slug. It refuses a second repository, and the declaration cannot be changed afterwards, because
either change would rename every file agents have been quoting.
_Avoid_: Simple project, mono-repo, flat project

**Refresh**:
Bringing a project's local copies up to date with their git remotes and rebuilding its index from
them. Happens on a schedule and on operator action. Agents read the index; they never cause a
refresh.
_Avoid_: Sync, reindex, update, pull

**Shadow Index**:
The replacement index a refresh builds alongside the one still serving queries. It takes over only
once complete, so a project is never searchable in a half-built state.
_Avoid_: Staging index, temp index, rebuild

**Durable Copy**:
What a project's index is stored as outside the container: its tables as Parquet under the project's
own prefix, written by the build that produced them and read back when the index file is absent. The
container's disk is wiped on every stop, so this is the only copy of an index that outlives a
replica. The control database has one too, kept as a file rather than as Parquet, and that one alone
is called a backup because nothing rebuilds it.
_Avoid_: Snapshot, archive, cache

**Warm-Up**:
Attaching every project ahead of the working day, so the first agent of the morning does not wait
for a restore. An external cron calls it where the server scales to zero, because a stopped server
has nothing running to fire one and the call is also what wakes it; a server kept running can also
warm itself on start. Either way it is one walk of every project, and the same walk.
_Avoid_: Preload, prefetch, cache warming

**Project Slug**:
The stable name identifying a project everywhere it is addressed — in its endpoint URL above all.
Distinct from the project's display name, which can change freely; the slug cannot, because agents
hold it in their configuration.
_Avoid_: Project name, id, key

**Repository Slug**:
The stable name identifying a repository within its project, and the first segment of every
qualified path in it. Assigned by an operator rather than derived from the git URL, so that moving
a remote does not rename the paths agents have been quoting. In a single-repository project it
heads no path, so the system assigns it and the operator is never asked for one.
_Avoid_: Repo name, folder name
