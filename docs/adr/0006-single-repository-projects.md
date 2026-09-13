# Single-repository projects name files without a repository slug

A project may be declared a **single-repository project** when it is created. Its files are named by
the path inside its one repository — `src/Widget.cs`, not `main/src/Widget.cs` — everywhere a
qualified path appears: the MCP tools, the API, the web UI, and the index itself.

The declaration is made at creation and is then read-only. A single-repository project refuses a
second repository, forever.

This narrows ADR-0001, which holds otherwise: a project is still the index boundary, still holds
repositories, and a multi-repository project is unchanged.

## Why

The repository slug earns its place when a project holds several repositories: it separates two
files that are both `src/index.ts`, and it survives a remote moving. In a project that holds one
repository it separates nothing. It is a segment every path carries, every agent quotes, and every
operator reads, to disambiguate a set of one.

`CONTEXT.md` previously said a project of one repository is "the common case, not a special case".
That was a statement about the data model, and it stays true of the model — one repository is still
a repository, indexed the same way. What changes is the naming, which was the part that made the
common case pay for the rare one.

## Why the flag cannot be changed afterwards

Turning it on later would rename every file in the project. Turning it off later would do the same
in reverse. A qualified path is the one string agents hold in their configuration and quote back at
us, and ADR-0002 already treats the project slug as immutable for exactly this reason. A path shape
that can change is a path shape that cannot be relied on, which would cost more than the slug ever
did.

So the choice is made once, with the project, and a single-repository project that turns out to need
a second repository is a new project.

## Consequences

- **A qualified path cannot be parsed without knowing its project.** `src/index.ts` is a file in a
  single-repository project and a repository named `src` in any other. Parsing therefore takes the
  project, not just the string, and `ProjectPaths` owns both the parse and the prose that explains
  the shape to an agent that got it wrong.
- **The repository still exists and still has a slug.** It is the project's slug, assigned by the
  system rather than by the operator, because a repository row needs a key and nothing reads it as
  a path segment any more. The operator is not asked for one.
- **Adding a second repository is a semantic failure**, answered with an explanation, not an
  exception. It is a thing an operator will try.
- **The index stores the short path.** `files.qualified_path` holds what the tools answer with, so
  no read path has to rewrite anything, and a `GLOB` an agent writes matches what it was shown.
- **Two shapes are now testable**, and the suite covers both. A test that only builds
  multi-repository projects would never see the short shape at all.
