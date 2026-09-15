# Modular monolith: one host project, one folder per module

The server stays one ASP.NET Core project, organised into folders that follow the modules named in
`CONTEXT.md`: `Control/`, `Git/`, `Index/`, `Search/`, `Refresh/`, `Operator/`, plus a single
`Telemetry.cs`. Endpoints remain thin and are registered from `Program.cs`, moving into a `MapX`
extension per module only when `Program.cs` outgrows inline lambdas. The alternatives weighed on
2026-09-13 were a flat project root, feature folders (vertical slices), clean architecture and
hexagonal ports and adapters.

The finished system is one deployable of roughly 25 to 40 C# files, seven MCP tools and about
fifteen operator endpoints, where almost all correctness is SQL and git plumbing. That shape
decided it:

- **Feature folders** promise one folder per ticket, but the tools here are not independent. Every
  one reads the same `lines` and `files` tables through the same connection checkout, the same
  `USE` rule and the same search-engine pinning. Slices would either duplicate that or grow a shared
  layer and stop being slices. Refresh, shadow swap and Parquet restore are one lifecycle, not three
  features, and would be split by endpoint.
- **Clean architecture** protects domain logic from infrastructure. There is almost no domain
  logic to protect: the domain is a slug, a URL and a path, and the behaviour is DuckDB, git and MCP
  transport, which the onion pushes to the outer ring and treats as replaceable. Nobody will replace
  DuckDB; ADR-0003 chose it for BM25 and Parquet, both engine-specific. Repository interfaces would
  hide the SQL the standards require to stay visible and inline-commented, and would break two
  written rules: no interfaces for mocking, tests against a real DuckDB.
- **Hexagonal** names the one seam this system does have, local folder versus Azure storage. Applied
  everywhere it costs the same as clean architecture. Applied to that one seam it is a single class
  choosing between two code paths in its constructor, which is what the "local default by absent
  configuration" rule already produces.
- **A flat root** is what the walking skeleton had. It is right at five files and wrong at forty,
  when a coding agent can only grep, not browse.

Module folders give the navigability feature folders promise, keep every existing rule, add no
dependency and no project, and put boundaries where the coupling really is.

## Shape

- `Control/`: `control.duckdb`, projects, repositories, credentials, their operator endpoints.
- `Git/`: clones, tree reading, URL classification.
- `Index/`: one DuckDB file per project, attach and `USE`, ingest, Parquet durability. The
  local-or-Azure decision lives in one class here, not behind an interface.
- `Search/`: the MCP tools and the line classifier they share.
- `Refresh/`: shadow build, drain and swap, and the refresh and warm-up endpoints.
- `Operator/`: authentication, status, anything that serves the web UI.
- One `CodeExplorer` namespace throughout. A folder is a boundary and a place to look, not a
  `using` line.

## Consequences

- Folders are a convention the compiler does not enforce. Once four modules exist, one reflection
  test asserts that `Search/` types reference nothing in `Control/`, turning the convention into a
  failing build.
- The tests project mirrors the folders, so a ticket's tests sit where its code sits.
- `Program.cs` is the composition root. It is allowed to grow to about 150 lines of inline
  endpoints; past that, each module's endpoint group moves to a `MapX` extension in its folder and
  `Program.cs` calls them. The one-statement handler rule does not change.
- The Parquet class is revisited after issue #9. If by then it has a second reason to change, that
  is the moment for an explicit port, and not before.

## Revisited after #9, on 2026-09-14

The revisit above came due when #9 was built. Two things held and one moved.

- **Still one class, still no port.** `DurableStore` chooses between Blob Storage and a folder from
  one nullable field set in its constructor, exactly as the Hexagonal paragraph predicted. It was
  briefly written as an abstract base with two implementations; that is a port by another name, it
  cost a virtual call per operation and a second type to read, and it bought nothing the branch does
  not. No second reason to change appeared, so no port.
- **It does not live in `Index/`.** The Shape entry above put it there, and that was written before
  the ticket showed that the control database's backup needs the same store (ADR-0004). `Index/`
  already depends on `Control/` — `IndexBuilder` reads a `ProjectRepository` — so putting the store
  in `Index/` would have made the two modules depend on each other. It sits at the repository root
  instead, beside `Telemetry.cs` and `Project.cs`, which is where this codebase already keeps what
  every module is handed. The Parquet itself, `DurableIndex`, is in `Index/` as the Shape says.
- **The warm-up is in `Refresh/`**, as the Shape says, and not in `Operator/`: it is work a cron
  drives against an index, not something that serves the web UI.

## Revisited for #35, on 2026-09-15

The root had grown to nine files — telemetry, the durable store, the key ring, authentication,
settings, the reader helpers, the qualified path, the project record and its route binding — and the
review behind #35 added a tenth: the one module through which every reader opens a project's index.
"What every module is handed" was a rule with no folder, so it read as a bucket of leftovers to a
coding agent grepping for a place to look, and the boundary test had nothing to name.

- **They live in `Infrastructure/` now.** One folder for code that no `CONTEXT.md` concept owns and
  that more than one module is handed. `Program.cs` alone stays at the root, because it is the
  composition root and not a module. `ToolReply` moved there too: its callers are in `Control/` as
  well as `Search/`, so a `Search/` home had `Control/` crossing a seam for a pluraliser.
- **The name is deliberately not a domain term.** Every other folder is; this one is the exception
  that lets the rule stay strict. `Control/` does not belong in it, even though every module depends
  on it: `Control/` is named after Project and Repository, holds endpoints and MCP tools, and is the
  module ADR-0005's guarded arrow points away from. Being the root of the dependency graph is not the
  same as being infrastructure.
- **The folder is a real seam because the boundary test says so.** `Infrastructure/` references
  nothing in `Search/`, `Refresh/` or `Operator/` — it may reach `Index/` and `Control/`, because
  the route binding already reads the control database and the index reader opens an index — and
  `Control/` and `Operator/` reference nothing in `Search/`. Without the assertions it is a bucket
  with a better name.
- The test project mirrors it: the tests of those files sit in `CodeExplorer.Tests/Infrastructure/`.
