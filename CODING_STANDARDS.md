# Coding standards

Rules a reviewer or an analyzer cannot infer from the code itself. Formatting, naming and style are
enforced by `.editorconfig` and the compiler — they are deliberately absent here. Generic code
quality is also absent: assume the reader already knows that duplicated code and mysterious names
are bad.

Read `CONTEXT.md` for vocabulary and `docs/adr/` for the decisions these rules follow from.

## Dependencies

- Every library and its major version is decided in ADR-0004. A ticket adds no dependency on its
  own; if it needs one, the ADR changes first.
- Regex and glob matching run inside DuckDB (RE2 syntax, SQL `GLOB`). .NET `Regex` is for
  classifying a candidate line, never for filtering the candidate set.
- **Every Azure dependency has a local default chosen by absent configuration**: a folder instead
  of Blob Storage, the default Data Protection key ring, authentication off, no exporter. A plain
  `dotnet run` with an empty `appsettings` must come up as a complete, offline, unauthenticated
  server. Nothing may require Azure to start.

## Layout

- Two C# projects: `CodeExplorer` (host, endpoints, storage, MCP tools) and `CodeExplorer.Tests`.
  The web app is `web/`, a Vite+ build into `CodeExplorer/wwwroot`, and stays out of the solution
  file.
- **Modular monolith (ADR-0005).** Inside the host, one folder per module, named after the concept
  in `CONTEXT.md` it owns: `Control/` (projects, repositories, credentials), `Git/` (local copies),
  `Index/`, `Reading/` (how an index is read: the reader, the shared statements and the row records
  every reading module is handed), `Search/`, `Refresh/`, `Operator/`, `Language/` (what a file is
  written in, and everything that follows from it — ADR-0008). Folders follow the module boundary, never the
  ticket or the endpoint; a refresh touches `Refresh/`, not a folder per endpoint. Everything stays
  in the single `CodeExplorer` namespace, so a folder is navigation and a boundary, not a `using`.
  A module reaches another only through its public types; `Search/` never opens `control.duckdb`.
  What no concept owns and more than one module is handed — telemetry, the durable store, the key
  ring, authentication, settings, the project record and its route binding, the outcome type every
  index-backed answer returns, the tool arguments and the tool reply — lives in `Infrastructure/`,
  the one folder not named after a concept, and nothing else does: reading an index is a module and
  has one (ADR-0005, revisited for #153). Only `Program.cs` stays at the root. `ModuleBoundaryTests` turns the arrows into
  a failing build: it sweeps every ordered pair of module folders against an allow-list, so an arrow
  nobody wrote down fails by default and the allow-list and ADR-0005's prose change together. A
  module outside `Index/` and `Refresh/` reaches an index through `IndexReaders` and never names
  `ProjectIndexes`. `Reading/` is where `IndexReaders` lives, and it is the only folder outside
  `Index/` that may name the attach-and-lease type. The test project mirrors the folders.
- Endpoints are inline lambdas in `Program.cs`, grouped with `MapGroup`. Logic lives in a service;
  a handler that needs more than one statement of its own is a handler doing too much. Once
  `Program.cs` passes about 150 lines, an endpoint group moves to a `static void MapX(this
  RouteGroupBuilder)` extension in its module's folder, and the handlers stay one statement each.
- A handler under `/api/projects/{project}` maps onto `api.MapProject()` and declares a `Project`
  parameter, never a slug. Whether the project exists is `BoundProject`'s question, asked once per
  request and answered with one sentence; a handler that asks it again is a handler on the wrong group.
- Group related DTOs at the top of the file that uses them. One type per file is not a rule here.
- No interfaces for the sake of mocking. Tests use a real DuckDB.

## Testing

- **Integration-first, against a real DuckDB.** Nearly all the correctness in this system is SQL:
  BM25 scoping, `repo_id` filtering, the shadow swap. A mocked database proves the mock works.
- xunit.v3 with its built-in `Assert`; no assertion library. The host runs in-process through
  `WebApplicationFactory`, and a test that talks MCP uses the SDK's own client.
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
  `RefreshService.Pending` does. Never a bare fire-and-forget.

## Storage

- Every connection runs `USE <project slug>` before querying, every time it is checked out. Never
  rely on a binding a connection already has: `DETACH` succeeds regardless of who is using the
  database, and the next statement on that connection fails with `Binder Error: Catalog does not
  exist!`. Connections themselves are pooled per project (ADR-0003, revisited for #149) — what may
  not be reused is the binding, not the socket — and whatever detaches a project's catalog empties
  that project's pool, because a pooled connection bound to a catalog that is gone is that same
  error handed to whoever borrows it next.
- Qualify nothing against `fts_main_lines`. `match_bm25` resolves its internal tables unqualified,
  so it works only against the current database (duckdb/duckdb#13523).
- No transaction writes to two projects. DuckDB forbids it and nothing here needs it.
- A refresh builds a shadow index and swaps. Never mutate a live index in place.
- Projects, repositories and credentials live in `control.duckdb`, never in a project index. The
  control database is not shadow-rebuilt and not exported to Parquet; it is backed up as a file.
- Annotate non-obvious SQL inline — why a join is skipped, why a value is safe to inline, what a
  CTE holds.

## Git

- LibGit2Sharp, never the git CLI. Credentials go through `CredentialsProvider` so a token never
  reaches process arguments or a URL on disk.
- A stored credential is `IDataProtector` ciphertext in the control database. It is write-only in
  the UI and the API (readable only as set or not set), and it never appears in a log, a span or an
  error message.
- Clones are bare and carry full history (ADR-0007; they were shallow until it). Read the file list
  from the HEAD tree and content from blobs; there is no working copy to walk, and therefore no
  `.gitignore` handling. A clone is kept between refreshes and is the largest thing on the ephemeral
  disk, so anything that adds to what a refresh transfers is sized by the free-space gate first.
- Refuse a repository that declares `filter=lfs`. libgit2 has no LFS support and would index
  pointer files as though they were source — a silent wrong answer, not an error.

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
- **The web app mirrors the server's module folders** (ADR-0005): `web/src/features/<concept>/`
  holds that concept's components and its query definitions, `web/src/routes/` the file-based
  routes, `web/src/components/` only what every feature uses, `web/src/lib/` the API client, the
  shared query-key roots and the URL contracts in `lib/urls/`, and `web/src/hooks/` what more than
  one feature reuses. A feature folder is a boundary, not a bucket. Neither `components/` nor `lib/`
  may import from `features/`, and a lint rule fails `vp check` on one that does, as
  `ModuleBoundaryTests` does for the server. `web/src/app/` is the frame — the sidebar, the top bar,
  the root layout and the navigation table — and is the one shared folder that may depend on
  features, since naming every view is what it is for.
- Shareable state lives in the URL as TanStack Router search params, typed with `validateSearch`,
  so a view can be linked. No state library. TanStack Query is the read cache and not an exception
  to that: nothing a link should carry may live only in it.
- A route loader primes a query with `ensureQueryData` and the component reads it with
  `useSuspenseQuery`. Writes are `useMutation` and invalidate a key built from `lib/queryKeys.ts`,
  never a key spelled out at the call site.
- Tailwind utility classes and shadcn components, in their Base UI flavour (ADR-0004): compose with
  `render={<Link … />}`, not Radix's `asChild`. A shadcn component, once copied into
  `src/components/ui`, is project source: edit it there and never regenerate over it. No global
  stylesheet beyond Tailwind's entry file, the theme tokens, and the `th-*` palette the highlighter
  needs — `@tanstack/highlight` emits class names onto spans this code never sees, so its colours
  cannot be utility classes and belong with the other tokens. Inline styles only for computed values.
- Syntax highlighting is `@tanstack/highlight` with the C# and X# definitions kept in this repo,
  sharing the pattern collector in `highlight/patterns.ts`. A language the library lacks gets a
  definition here, not a second highlighter, and gets tests: a bad pattern mis-colours a file
  instead of throwing, so pattern order is asserted rather than eyeballed.
- `vp check` and `vp test` are the gate (ADR-0004). Lint rules are configured in `vite.config.ts`;
  a rule switched off carries the decision it conflicts with, never "it was noisy".
- React Compiler is on, so memoisation is its job: no hand-written `useMemo` or `useCallback`. One
  kept anyway carries a comment naming the measurement that justified it — the profile that showed
  the compiler missing it, not an assumption that a render was slow. The exception is a value a
  lint rule demands be stable, such as a context `value` in `components/ui`, where the rule and not
  the compiler decides.
