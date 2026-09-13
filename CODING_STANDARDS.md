# Coding standards

Rules a reviewer or an analyzer cannot infer from the code itself. Formatting, naming and style are
enforced by `.editorconfig` and the compiler — they are deliberately absent here. Generic code
quality is also absent: assume the reader already knows that duplicated code and mysterious names
are bad.

Read `CONTEXT.md` for vocabulary and `docs/adr/` for the decisions these rules follow from.

## Layout

- Two C# projects: `CodeExplorer.Api` (host, endpoints, storage, MCP tools) and `CodeExplorer.Tests`.
  The web app is a Vite build into `CodeExplorer.Api/wwwroot` and stays out of the solution file.
- Endpoints are inline lambdas in `Program.cs`, grouped with `MapGroup`. Logic lives in a service;
  a handler that needs more than one statement of its own is a handler doing too much.
- Group related DTOs at the top of the file that uses them. One type per file is not a rule here.
- No interfaces for the sake of mocking. Tests use a real DuckDB.

## Testing

- **Integration-first, against a real DuckDB.** Nearly all the correctness in this system is SQL:
  BM25 scoping, `repo_id` filtering, the shadow swap. A mocked database proves the mock works.
- Each test class gets its own temp directory and real database file, deleted on dispose. Never
  `:memory:` — attach, `USE`, shadow-and-swap and Parquet restore are all about files.
- **Pin the search engine in every search test, and cover both paths.** `INSTALL fts` fails offline
  and silently leaves `FtsAvailable = false`, so an unpinned suite tests full-text search on a
  laptop and a substring scan on CI, and the two rank results differently.
- Cover the states a client can catch the server in, not just the happy path: project restoring,
  rebuild in progress, a swap mid-query.

## Errors

Two kinds of failure, two mechanisms. Never mix them.

- **Semantic failure returns a normal result**: a plain string saying what went wrong and what to
  try instead. No matches, an unknown project, a malformed pattern, an index still restoring — all
  of these are answers. Malformed input must never look like a real negative: an empty result is an
  answer the caller acts on, so an error that arrives shaped like one teaches the agent something
  false.
- **Infrastructure failure throws** `McpException`, with the remediation in the message.
- Distinguish "no results" from "results existed and filters excluded them". They read identically
  to an agent and mean opposite things.
- API errors return `new { error = "..." }` with the matching status code. No `ProblemDetails`, no
  Result type, no exception middleware.
- Every swallowed exception carries a comment saying why it is safe to swallow.

## Async and cancellation

- `CancellationToken` on every async path, threaded through to the DuckDB command. An agent that
  abandons a slow search must not leave a query burning one of two cores on a shared replica.
- No `.Result`, no `.Wait()`, no `GetAwaiter().GetResult()`.
- Background work is handed off explicitly — expose the `Task` so callers can await it, as
  `Database.PendingFtsBuild` does. Never a bare fire-and-forget.

## Storage

- Every connection runs `USE <project slug>` before querying. Unqualified `match_bm25` resolves
  against the current database only.
- No transaction writes to two projects. DuckDB forbids it and nothing here needs it.
- A refresh builds a shadow index and swaps. Never mutate a live index in place.
- Annotate non-obvious SQL inline — why a join is skipped, why a value is safe to inline, what a
  CTE holds.

## Telemetry

- One static `Telemetry` class holds every instrument. One `ServiceName` constant names the
  `ActivitySource`, the `Meter`, and their registrations.
- Metric and tag names are dotted and prefixed `codeexplorer.`.
- **Every span and metric carries the project slug.** With several projects on one replica,
  untagged telemetry cannot answer which department is slow.
- Recording goes through one chokepoint per operation, so a new entry point cannot report a
  different set of attributes than the existing ones.

## Comments

- Comment the decision, not the mechanics. `// open the connection` is noise; why this connection is
  opened here rather than reused is not.
- When code exists because something else did not work, say what did not work.
- Document every `const` whose value is a judgement call, with the judgement.
- `[Description]` text on MCP tools is agent-facing prose and is part of the product. Write it as
  carefully as the code. Tool names match the shell verbs a model already knows — `grep`, `glob`,
  `list_tree` — so the prior transfers.

## TypeScript

- `strict` is on. (The proof of concept ran without it while writing code that would have passed
  anyway; there is no reason to inherit the gap.)
- Named function declarations for components, not arrow consts. One component per file, PascalCase
  filename matching the export.
- Shareable state lives in the URL via `useSearchParams`, so a view can be linked. No state library.
- One global stylesheet, semantic kebab-case class names. Inline styles only for computed values.
