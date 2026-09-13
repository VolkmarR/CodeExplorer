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
refreshes. A project of one repository is the common case, not a special case.
_Avoid_: Source, codebase

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
without collision.
_Avoid_: Full path, absolute path

**Refresh**:
Bringing a repository's local copy, and the part of the index built from it, up to date with its
git remote. Happens on a schedule and on operator action. Agents read the index; they never cause
a refresh.
_Avoid_: Sync, reindex, update, pull
