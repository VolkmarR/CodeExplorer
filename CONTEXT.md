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
An argument that scopes an answer to one is spelled `repo` on the MCP tool surface and `repository`
on the HTTP API: one concept, and two surfaces whose callers are a model reading tool schemas and a
web app nobody retypes. A new scoped tool or endpoint follows the surface it is on rather than
choosing, and neither surface accepts both.
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

A reference is of a **symbol** and never of a file. There is no such thing here as "the references
of `Widget.cs`": answering that would mean asking the question of every name the file declares, a
scan of the project per file opened, for a list that would still be evidence about names rather than
about the file. What refers to a *file* is an import, and the answer to it is the file's dependents.
_Avoid_: Usage, call site, occurrence

**Declaration**:
A line that introduces a name: a type, a routine, a member. Read from the shape of the line in the
language the file is written in, so it is evidence of the same strength as a reference and never
proof, and a form no language profile knows is a declaration this does not find rather than one
that is not there. Where a language announces a routine in one place and writes it in another —
Delphi's `interface` against its `implementation`, a PL/SQL package spec against its body — both
lines are declarations and each says which of the two it is; where the two coincide, it says
nothing rather than picking one.
_Avoid_: Definition (except as the name of the tool that answers with declarations), signature,
symbol

**Import**:
One file naming another it depends on: a `using`, a `#include`, a `uses` clause, a `<script src>`.
The name is recorded as the file wrote it and then resolved to a file in the project where the
mapping is unambiguous, which is what makes "who depends on this" answerable at all. Read from the
shape of the line in the language the file is written in, so it is evidence of the same strength as
a reference and never proof, and a form no language profile knows is an import this does not find
rather than one that is not there. A name that resolves to nothing, or to several files, stays in
the answer as the name it was and says so: an import left out reads as a dependency the file does
not have. A language with no import concept — the SQL family — is told apart from a file that
imports nothing, because an empty answer would otherwise read as "this depends on nothing".
_Avoid_: Dependency, include, reference (which is a different thing here), edge

**Commit**:
One recorded change on a repository's default branch, identified by its SHA and carrying the author
who wrote it. Merges count as one commit and their side branches are not walked (ADR-0007), so a
pull request reads as a single change.
_Avoid_: Revision, changeset, changelist

**History**:
The commits a project has imported for a repository, which is never guaranteed to be complete: a
repository may have none at all, and a file's history begins where it was last renamed. An agent
told nothing is told that, rather than being given an empty answer.
_Avoid_: Log, git history, timeline, audit trail

**Author**:
Whoever a commit records as having written it, identified by their email address and never by the
display name beside it: one person commits under several spellings of their name from one address,
and two people share a first name. So a ranking groups by address, a person committing from two
addresses is two authors, and `git_log`'s `author` filter matches the address — `authors` is what
names them. Author and not committer: a rebase makes the two disagree.
_Avoid_: Contributor, owner, committer, developer

**Attribution**:
The commit a line or a file was last changed by. Evidence of who touched something last and not of
who wrote the logic — a reformat is an attribution — so it answers "who worked on this" and never
"when was this introduced".
_Avoid_: Blame, ownership, authorship

**Churn**:
How much a file moved over a span of history: the commits that touched it and the lines those added
and removed. It measures where work happened and not where the logic changed — a reformat is churn,
the same way it is an attribution — so it answers "what is this project busy with" and never "what
is unstable". A path that churned and is no longer at HEAD is churn that happened.
A directory's churn is the distinct commits that touched anything beneath it, never the sum over the
files in it: one commit touching forty of them changed that directory once, and the sum would say
forty — wrong in the direction that decides which module looks like it is moving.
_Avoid_: Hotness, activity, volatility, code age

**Co-Change**:
Two files having been committed together, counted over a window. It is the coupling the code does
not show — a constant and the three places that read it, a stored procedure and the class that calls
it — and it is evidence rather than proof, because two files in one reformat share a commit without
sharing anything else. Pairing never crosses a repository, because a commit does not, and commits
that touched more paths than a configured ceiling are left out of it: a mass commit pairs every path
it touched with every other, which is one commit and not a relationship between any two files in it.
_Avoid_: Coupling, correlation, related files, change coupling

**Overview**:
What a project is, in one answer: its repositories and where each stands, what it is written in, how
it is laid out, what is largest in it, where work has been happening and who has been doing it.
Computed by the build that produced the index and stored with it, so it describes the index rather
than the repositories as they are now, and so a caller pays one row read however large the project
is. It is the first thing an agent asks and the first thing an operator sees, and it is one answer
because a reader who has to assemble it from a dozen calls has already formed a wrong picture.
_Avoid_: Summary, stats, dashboard, profile

**Language**:
What a file is written in, resolved from its extension. Counts are reported by language rather than
by extension because "53% X#" tells a reader what "42% .prg, 11% .vh" does not. An extension no
profile covers stands for itself instead of being guessed at: a vaguer answer costs a reader a
lookup, an invented one costs them a wrong belief.
_Avoid_: File type, dialect, tech stack

**Window**:
The span of history a question is asked over, anchored to the newest commit imported rather than to
the clock. An index is built by a refresh and may be behind its remotes, so "the last thirty days"
counted from today would answer for a stale index with an empty result — which reads as "nothing
changed", the one thing history must never say by accident. Counted back from the newest recorded
commit it answers with the last thirty days there were, and says which dates those are.
_Avoid_: Range, period, since, time frame

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
