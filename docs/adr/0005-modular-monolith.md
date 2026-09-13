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
