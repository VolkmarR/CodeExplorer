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
- `Git/`: clones and tree reading. URL classification moved to `Control/` in the #146 revisit, and
  on to `Infrastructure/` for GHSA-5373-pppr-q3q9.
- `Index/`: one DuckDB file per project, attach and `USE`, ingest, Parquet durability. The
  local-or-Azure decision lives in one class here, not behind an interface.
- `Search/`: the MCP tools and the line classifier they share.
- `Refresh/`: shadow build, drain and swap, and the refresh and warm-up endpoints.
- `Operator/`: authentication, status, anything that serves the web UI.
- One `CodeExplorer` namespace throughout. A folder is a boundary and a place to look, not a
  `using` line.

## Consequences

- Folders are a convention the compiler does not enforce. Once four modules exist, one test turns the
  convention into a failing build. It began as a single assertion that `Search/` types reference
  nothing in `Control/`; it is a sweep over every ordered pair of module folders since the #146
  revisit below.
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
  depended on `Control/` at the time — `IndexBuilder` read a `ProjectRepository` — so putting the
  store in `Index/` would have made the two modules depend on each other. (That arrow is gone as of
  the #146 revisit: the record moved to `Infrastructure/`.) It sits at the repository root
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

## Revisited on 2026-09-18: the outcome type joins `Infrastructure/`

`SearchOutcome` and `SearchProblem` were declared in `Search/`, so the index reader could hand a
caller a refusal only as a string — its own doc apologised for it — and `Control/` and `Operator/`,
which refuse in the same words when an index is missing, could not name the type at all. Every module
that opens an index refuses the same way, which is the definition of "what more than one module is
handed": the pair now lives in `Infrastructure/Outcome.cs` as `Outcome` and `Problem`, the names no
longer claiming a search is the only thing that has one. `Problem` carries a `ProblemKind`, because
HTTP answers a missing index with a 404 the browse view draws as "nothing to browse yet" and every
other problem with a 400, and that is the one fact about a problem a renderer needs beyond its prose.
The result records stay in `Search/` and derive from `Outcome`; the boundary test is unchanged.

## Revisited for #146, on 2026-09-20: the arrows are enumerated, and two cycles are gone

The boundary test was six hand-written facts, so the arrows it did not name were unguarded, and two
of them had closed cycles. It is now one theory over every ordered pair of the eight module folders
against an explicit allow-list, which makes failing the default: an arrow nobody decided on fails
the build the moment it is drawn.

- **`Control/` ↔ `Index/` is gone.** The scope combinators — over an index, over a file, over a
  directory — were static methods on `IndexReader` taking `ProjectIndexes` as their first argument,
  so every caller named the attach-and-lease type: nine `Search/` files, plus `Control/`, `Operator/`
  and `Refresh/`. `Control/` naming it closed a cycle with the `Index/` → `Control/` arrow this ADR
  allowed. They are instance methods on an injected `IndexReaders` now, and
  `Infrastructure/IndexReaders.cs` is the only module file outside `Index/` and `Refresh/` that
  references the type — which is the one arrow this ADR already granted `Infrastructure/`, now down
  to one file rather than spread across three modules. `Program.cs` registers it and is the
  composition root, not a module; the sweep does not walk it.
- **`Git/` ↔ `Control/` is gone.** `Git/` read `ProjectRepository` and the credential purpose out of
  `Control/` while `Control/` read `Git/`'s URL classifier. The record moved to
  `Infrastructure/Project.cs`, beside the project record it is the other half of, and the purpose
  string to `KeyRing`, whose own doc already said Control protects credentials with it and Git
  unprotects them. The classifier went the other way, into `Control/`: it sat in `Git/` because the
  clone read it to decide whether it could be shallow, ADR-0007 made every clone full and took that
  reader away, and what was left is the API's validation of a field `Control/` owns. The two modules
  now name nothing of each other's in either direction.
- **`Index/` → `Control/` went with it**, unplanned. The repository record was the only thing the
  build read there, so moving it left `Index/` naming nothing in `Control/`. The Shape section above
  still says `Index/` already depends on `Control/`; as of this revisit it does not.
- **`Search/` → `Index/` was also real, backwards.** The `imports` table's `shape` and `evidence`
  columns were spelled on `ImportBuilder`, so the import tools named a build type to decode a column
  they had already read. The codec is `Infrastructure/ImportColumns.cs`: the column is what the two
  modules share, so the column's spelling is what they are handed. That gives `Infrastructure/` an
  arrow to `Language/`, which is fine and was always implied — being the leaf (ADR-0008) is what lets
  anything reach it.
- **The match is qualified to type positions.** A bare `\bName\b` was fine over six curated pairs and
  is wrong over fifty-six: `Reference`, `Answer`, `Declared` and `Hole` are top-level types here and
  also ordinary property and variable names elsewhere. The sweep now looks for the positions C# puts
  a type in — after `new`, in a generic argument, in a base list, in a declaration, before a `.`, in
  a cast — skips nested types, whose bare use inside their own file no text can tell from a
  reference, and skips any name the reading file declares itself. Ten `[InlineData]` cases assert
  both halves of that, because a detector that has stopped matching reports exactly what a clean tree
  reports.

The arrows this leaves, which are the allow-list in `ModuleBoundaryTests` spelled out — anything not
on this list fails the build, and an arrow on it that nobody draws fails it too, so the list and this
paragraph are edited together:

| from             | may reference                      |
| ---------------- | ---------------------------------- |
| `Control/`       | `Infrastructure/`                  |
| `Git/`           | `Infrastructure/`                  |
| `Index/`         | `Git/`, `Infrastructure/`, `Language/` |
| `Infrastructure/`| `Control/`, `Index/`, `Language/`  |
| `Language/`      | nothing                            |
| `Operator/`      | `Control/`, `Git/`, `Infrastructure/` |
| `Refresh/`       | `Control/`, `Git/`, `Index/`, `Infrastructure/` |
| `Search/`        | `Infrastructure/`, `Language/`     |

The module folders are read off the source tree rather than listed, so a new one arrives with no
allowed arrows at all and its first reference in either direction has to be argued for here.

## Revisited for #153, on 2026-09-20: reading an index is a module

`Infrastructure/` was defined above as what no concept owns and more than one module is handed. It
had grown to 4,200 lines, and 2,300 of them were one thing: the index reader, the statements every
reader shares, the query and plan helpers, the reader columns, the overview document, the history
window and the path terms. Reading is named nowhere in the Shape list, so it landed in the folder
that is not named after a concept — and two of those files said as much in their own doc comments,
justifying where they sat with "the boundary test would fail anywhere else". That is a module with
no folder, which is the same fault the #35 revisit fixed for the root.

- **`Reading/` is a module.** It holds how an index is read: `IndexReader` with its row records and
  its tree listing, `IndexReaders` — the one way in — the shared statements, the query and plan
  helpers, the reader columns, the overview document, the history window, the path terms and the
  qualified-path rule. It is named after the act and not after a noun in `CONTEXT.md`, because the
  noun it would be named after is Index, and that name is taken by the module that writes one.
- **It is not `Index/`.** `Index/` builds: it clones, ingests, attaches, shadows and swaps, and it
  reaches `Git/` to do it. A reader that lived there would put every MCP tool one arrow away from
  the builder and the local copy, and `Search/` would then reach `Index/` — the arrow this ADR has
  refused since the first version of the boundary test, because an answer comes from what the last
  build read and never from a remote.
- **It is not `Search/`.** `Control/` and `Operator/` read indexes too — the project page, the
  overview reply and the browse view all do — and both are forbidden to reach `Search/`, which is
  where the #146 revisit found `Control/` reaching for a pluraliser. Reading is what three modules
  are handed; searching is one caller of it.
- **`Infrastructure/` is the ten files that really are plumbing**: telemetry, the durable store, the
  key ring, authentication, settings, the project record and its route binding, the outcome type,
  the tool arguments and the tool reply. The refresh progress record went to `Index/` rather than
  here: the steps that can count their work are the build's, and `Refresh/` already reaches `Index/`.
  What is left is host plumbing under any reading of the word, which is the test of the folder the
  #35 revisit asked for and could not apply while a reader lived in it.
- **The two arrows out of `Reading/` are argued, not inherited.** `Reading/` → `Index/` is
  `IndexReaders` naming `ProjectIndexes`, still exactly one file, which is the single arrow this ADR
  granted `Infrastructure/` at #146 and which moved with the file. `Index/` points back, because a
  build writes the rows a read parses — the overview document, the imports columns, the qualified
  path — and two spellings of one column is a disagreement baked into the index rather than a
  failing read. The pair is a cycle between the folder that writes an index and the folder that
  reads it, and it is narrower than the one it replaces: `Infrastructure/` had the same pair while
  also holding authentication and the key ring.
- **`Infrastructure/` → `Reading/` is one file.** `ToolReply` draws a churn row and an author row
  for three surfaces at once, so it names the two records `Reading/` declares. The alternative was a
  third spelling of a ranked row, which is the drift the shared renderer exists to prevent.

Before the move the three files that had outgrown reading were split by concern, as pure moves:
`IndexReader` into its row records, the scope and locating and advice, and the tree listing;
`HistoryQueries` into the commit listings, the reads about one file or one commit, the rename
lineage and the co-change pairing; `ProjectIndexes` into the attach-and-swap machinery and the DDL
with the schema version it is stamped with. `HistoryTools` and `SearchTools` were split the same way
for the same reason. No file in `Reading/` or `Search/` is much past 600 lines now.

The arrows this leaves, which supersede the table in the #146 revisit above and are the allow-list
in `ModuleBoundaryTests` spelled out — anything not on this list fails the build, and an arrow on it
that nobody draws fails it too, so the list and this paragraph are edited together:

| from              | may reference                                      |
| ----------------- | -------------------------------------------------- |
| `Control/`        | `Infrastructure/`, `Reading/`                      |
| `Git/`            | `Infrastructure/`                                  |
| `Index/`          | `Git/`, `Infrastructure/`, `Language/`, `Reading/` |
| `Infrastructure/` | `Control/`, `Reading/`                             |
| `Language/`       | nothing                                            |
| `Operator/`       | `Control/`, `Git/`, `Infrastructure/`, `Reading/`  |
| `Reading/`        | `Index/`, `Infrastructure/`, `Language/`           |
| `Refresh/`        | `Control/`, `Git/`, `Index/`, `Infrastructure/`    |
| `Search/`         | `Infrastructure/`, `Language/`, `Reading/`         |

`Infrastructure/` → `Index/` and `Infrastructure/` → `Language/` are gone from it, not because
anything stopped being true but because both arrows were drawn by files that are now in `Reading/`.
The test project mirrors the new folder: `CodeExplorer.Tests/Reading/` holds the tests of the tools
that are nothing but a read — `read_file`, `glob`, `list_tree`, `list_extensions` and `repo_info`.

## Revisited on 2026-09-23: a module is a namespace

The Shape above said one `CodeExplorer` namespace throughout, so that a folder was a boundary and a
place to look but never a `using` line. That no longer holds: every module folder is now its own
namespace — `CodeExplorer.Control`, `CodeExplorer.Git`, `CodeExplorer.Index` and so on, matching
the folder — and a file that crosses a module says so in a `using` at its top.

- **The arrows are unchanged.** The table above is still the allow-list. A C# namespace restricts
  nothing, so the compiler still enforces no arrow and `ModuleBoundaryTests` is still what does.
- **A `using` is now evidence the test reads.** IDE0005 fails the build on an unused one, so every
  `using CodeExplorer.<Module>;` is an arrow the compiler has confirmed, including one no text
  pattern sees, such as an extension method. The test counts it, and it counts a name qualified by
  its module (`Control.ControlDatabase`), which needs no `using` from inside another `CodeExplorer`
  namespace and which the type positions refuse for its leading `.`.
- **A doc comment is still not an arrow.** A cross-module `<see cref>` is written fully qualified
  (`CodeExplorer.Search.FileFilter`) rather than through a `using`: comments are stripped before
  the sweep, and a `using` kept alive only by a cref would otherwise read as an arrow nobody draws
  in code. Three did at the move — `Language/` → `Reading/`, `Reading/` → `Search/` and `Search/` →
  `Index/` — and none of them was a reference the table allows.

## Revisited for GHSA-5373-pppr-q3q9, on 2026-09-25: the URL classifier joins `Infrastructure/`

Local repositories became a setting, `Control:AllowLocalRepositories`, off by default, and two
modules now have to ask whether a URL is on the server's own disk: `Control/` when a repository is
added, and `Git/` when a stored one is about to be cloned or fetched after the setting was switched
off. `Git/` may not reach `Control/`, and two classifiers could disagree about what counts as local,
which is a way around the setting. So `RepositoryUrl` moved to `Infrastructure/`, where what more
than one module is handed lives, with the setting's name and its refusal sentence beside it. The
arrows are unchanged: both modules already reached `Infrastructure/`.
