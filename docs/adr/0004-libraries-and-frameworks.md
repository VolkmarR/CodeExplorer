# Libraries and frameworks

Every third-party dependency the tickets need, decided in one pass on 2026-09-13 so that no ticket
picks a library on its own. Versions are the latest stable on that date; a ticket bumps a version
freely within the same major and records anything larger here.

## Server

- **.NET 10** (`net10.0`), one `.slnx`, `Directory.Build.props` carrying `TreatWarningsAsErrors`.
  Current LTS and what the `CodeSearch` proof of concept already targets.
- **ModelContextProtocol 2.2.0 + ModelContextProtocol.AspNetCore 2.2.0**, Streamable HTTP
  transport, `MapMcp` on the `/projects/{slug}/mcp` route pattern. The SDK's `AddMcp`
  authentication scheme serves protected-resource metadata: with no `ResourceMetadataUri`
  configured, its handler appends the request path to `/.well-known/oauth-protected-resource` on
  challenge and answers any path under that prefix, deriving the resource URL from it. That is the
  path-scoped behaviour ADR-0002 requires, verified by decompiling 2.2.0; `OnResourceMetadataRequest`
  is where an unknown slug becomes a 404.
- **DuckDB.NET.Data.Full 1.5.5** and **LibGit2Sharp 0.32.0**, as ADR-0003 verified.
- **Matching runs inside DuckDB**: `regexp_matches` / `regexp_extract_all` (RE2 syntax) for grep,
  `list_matches` and `find_references` candidates, and the SQL `GLOB` operator on the path column.
  Filtering happens before rows leave the engine. Agents therefore get RE2 — no lookbehind, no
  backreferences — and every tool description says so. .NET `Regex` is used only to classify a
  candidate line as code, comment, string or import, as the proof of concept's `ReferenceFinder`
  does.
- **Microsoft.Identity.Web 4.14.2** for both the bearer API (`AddMicrosoftIdentityWebApi`) and the
  web UI's cookie sign-in (`AddMicrosoftIdentityWebApp`). The UI is a backend-for-frontend: the
  browser holds a cookie and never sees a token, sign-in and sign-out are two minimal endpoints,
  and the API accepts either the cookie or a bearer token. `Microsoft.Identity.Web.UI` is not used;
  it ships Razor controllers an SPA has no use for.
- **ASP.NET Core Data Protection with Azure.Extensions.AspNetCore.DataProtection.Blobs 1.5.4 and
  .Keys 1.6.4.** The key ring is persisted to Blob Storage and wrapped by Key Vault because the
  container disk is wiped on every stop; without this, every scale-to-zero restart signs every
  operator out. The same protector encrypts repository credentials at rest.
- **Azure.Storage.Blobs 12.29.2 + Azure.Identity 1.21.0** for Parquet durability. DuckDB writes
  Parquet to the local disk with `COPY TO`, the app moves it with the Blob SDK under the managed
  identity. Chosen over DuckDB's `azure` extension because write support there is unverified on
  1.5.5 and would be a second runtime extension install.
- **OpenTelemetry 1.19.0**: `Exporter.OpenTelemetryProtocol`, `Extensions.Hosting`,
  `Instrumentation.AspNetCore`, `Instrumentation.Http`, `Instrumentation.Runtime`. OTLP as in the
  proof of concept, vendor-neutral; telemetry is off when no endpoint is configured.
- **No scheduler library.** The app scales to zero, so an in-process timer cannot fire the warm-up
  or a scheduled refresh. The API exposes operator endpoints for refresh and warm-up and a
  Container Apps Job on a cron calls them. Progress reaches the web UI by polling a status
  endpoint. Amended on 2026-09-14: still no library, but a `BackgroundService` may now run the
  warm-up in process — see the note below, which says what that can and cannot replace.
- **xunit.v3 4.0.1** with its built-in `Assert`, **Microsoft.AspNetCore.Mvc.Testing 10.0.12** for
  the in-process host, and the MCP SDK's own client for tests that connect to an endpoint. No
  assertion library.

## Storage of configuration and credentials

Projects, repositories and repository credentials live in a **control database**, `control.duckdb`,
attached alongside the project indexes and managed from the web UI. It is never shadow-rebuilt and
never exported to Parquet; it is backed up to its own blob prefix as a file. Credentials are stored
as `IDataProtector` ciphertext, are write-only in the UI (shown only as set or not set), never
logged, and reach libgit2 only through `CredentialsProvider`. Anyone who could decrypt them needs
the control database, the key ring in Blob Storage and unwrap rights on the Key Vault key — which
is the app's managed identity and nothing else.

## Container

A **Dockerfile** whose build stage runs `INSTALL fts`, so the extension sits in the image and the
container never reaches out at runtime. `INSTALL fts` fails silently offline and would otherwise
downgrade a whole replica to substring scan on an egress restriction or a transient fault. The base
image needs no git binary; LibGit2Sharp bundles libgit2.

Built in #14, and two things about the bake are worth recording because neither is obvious from the
Dockerfile alone. **The install runs the published application** — `dotnet CodeExplorer.dll
--install-fts <directory>`, a switch that installs and exits — rather than a tool or a download of
its own: a DuckDB extension is stamped with the version and platform of the build that will load it,
and only that build knows both. **Where it lands is a setting**, `Index:ExtensionDirectory`, set in
the image and absent everywhere else, where DuckDB's own folder under the user profile is the better
answer because it is shared across checkouts. An extension directory that no deployment can name
would have to be `$HOME/.duckdb`, which means the build stage and the runtime stage agreeing on a
home directory by convention — the kind of coupling nothing in the file would have said out loud.

## Local development stays complete without Azure

Every Azure service above is selected by the presence of its configuration, and its absence selects
a local default: a folder on disk in place of Blob Storage (Parquet, the control database backup,
the key ring), the framework's default Data Protection key ring, authentication off, no telemetry
exporter, and `INSTALL fts` at runtime with the substring-scan fallback when offline. A plain
`dotnet run` with an empty `appsettings` is therefore a complete, offline, unauthenticated server,
and a ticket that breaks that has a defect.

## Web UI

Revised on 2026-09-13 while #7 was being built. The first pass named plain Vite 8.3 with oxlint as a
separate dependency, TanStack Router loaders as the only data layer, and `fetch`. The five decisions
below replaced that, and the reasoning for each is recorded here rather than in the ticket.

- **React 19.3, TypeScript 7.0, pnpm**, built into `CodeExplorer/wwwroot`. The app lives in `web/`
  at the repository root and stays out of the `.slnx`.
- **Vite+ 0.3.1 (`vite-plus`, the `vp` CLI)** in place of Vite and oxlint as separate dependencies.
  It bundles Vite 8.2.2 on Rolldown, Vitest, oxlint and oxfmt behind one dependency and one
  configuration file, `vite.config.ts`, which now also carries the `lint` and `fmt` sections. The
  cost is that the bundled Vite trails the standalone release by a patch or two; the gain is that
  formatting arrives at all, which a separate oxfmt would have been a sixth dependency to get.
  There are deliberately **no `package.json` scripts**: `vp dev`, `vp build` and `vp check` are
  built-ins, and a script of the same name would only make `vp run <name>` ambiguous.
- **TanStack Router 1.170** (`@tanstack/react-router` + `@tanstack/router-plugin`): file-based
  routes under `web/src/routes`, typed search params via `validateSearch`. Shareable state lives in
  the URL through the router's search params; there is no state library.
- **TanStack Query 5.102 and ky 2.1** for reads and writes. A route loader primes a query with
  `ensureQueryData` and the component reads it with `useSuspenseQuery`, so preloading on hover and
  invalidation after a mutation are one mechanism rather than two. ky replaces hand-rolled `fetch`;
  its `beforeError` hook is where the API's `{ error }` prose replaces the status line, which is the
  whole reason a semantic failure is worth returning as prose. Query is a cache, not the state
  library the paragraph above rules out: nothing shareable is kept in it.
- **Tailwind CSS 4.3** (`@tailwindcss/vite`) with **shadcn 4.21** components, in its **Base UI**
  flavour: `components.json` carries `"style": "base-vega"`, so `shadcn add` copies the components
  built on **@base-ui/react 1.8** rather than the Radix ones. Base UI composes through a `render`
  prop where Radix took `asChild` and a single child, which is the only difference the calling code
  sees. shadcn copies component source into the repo; those files are ours to edit and are not
  reinstalled over local changes — re-running `shadcn add --overwrite` discards local edits and
  reintroduces its `import { cn } from "cn"` mistake, which is ours to fix each time.
  What it copies is not self-contained, so its runtime dependencies are named here too and are not a
  ticket's choice: **@base-ui/react 1.8** (the primitives), **class-variance-authority 0.7** (the
  variant tables), **clsx 2.1** and **tailwind-merge 3.4** (the `cn` helper), and **lucide-react
  0.552** (the icon set `components.json` selects).
- **Fontsource 5.3** for the two typefaces the UI sets: **Geist** for text and **JetBrains Mono** for
  paths and code, as `@fontsource-variable/geist` and `@fontsource-variable/jetbrains-mono`. Bundled
  rather than linked from Google Fonts, because a plain `dotnet run` must be a complete offline
  server (CODING_STANDARDS, Dependencies) and a font that fails to load falls back silently, so an
  operator on an isolated network would see a different UI without an error saying why.
- **React Compiler through `oxc-transform-react`** (`@vitejs/plugin-react`'s `compiler: true`),
  the Rust port, not the Babel plugin — Babel is the only thing this toolchain would otherwise have
  had to install. `@vitejs/plugin-react` calls its native compiler support experimental; a fatal
  diagnostic fails the transform rather than miscompiling, so the failure mode is a build error.
  Manual memoization is not removed where it documents a real cost, such as tokenizing a whole file.
- **@tanstack/highlight 0.1.0** for the file view, with **custom C# and X# language definitions**
  written in this repo. Chosen knowing that 0.1.0 ships neither and has no grammar engine; both are
  hand-written patterns and candidates for upstream contribution. Its `patternTokenizer` helper is
  not in the package's exports map, so `highlight/patterns.ts` carries a thirty-line copy of that
  rule and both definitions share it.
- **@tanstack/react-virtual 3.14** for the file view, added on 2026-09-20 with #151. The index takes
  files up to 4 MiB, and drawing one as a row per line is a hundred thousand rows of three cells that
  the browser lays out before it paints anything. Only the rows on screen are rendered now, which is
  also what turned a line deep-link from a ref on the row — it has to be mounted to scroll itself
  into view, and it no longer is — into `scrollToIndex`, an instruction the virtualiser can carry out
  for a row that does not exist yet. Same family as the router and the query client, one dependency
  with no runtime of its own, and headless: the markup, the measurement and the scrolling stay here.
  It is the one library React Compiler refuses to compile a component around — it returns functions
  it replaces as it measures, and a memoized copy would report a scroll position that has passed — so
  `CodeView` is not compiled and says why at the call. Colouring follows the same rule as the rows and
  is done per block of lines under it (`highlight/lines.ts`), because tokenizing 4 MiB up front costs
  a third of a second before anything is drawn. `@tanstack/highlight` is also no longer imported as
  `allLanguages`: the grammars are imported one by one, so the chunk holds the ones the extension map
  can actually name and not the nine it cannot.
- **oxlint-plugin-react-doctor 0.9** on top of oxlint's own plugins, run through Vite+'s `jsPlugins`.
  Its 906 rules ship with no preset, so `web/lint.rules.ts` derives the enabled set from the
  plugin's own registry rather than listing them, at the severity each rule declares, minus rules
  for frameworks this app does not use and rules that need the `react-doctor` CLI's whole-project
  view. It earned its place on the first run by finding a real defect: a search form that copied the
  URL's parameters into state once and went stale when the pager navigated.

## The in-process warm-up, added on 2026-09-14

Asked for after #9 shipped, and added as `WarmUpService`, a framework `BackgroundService` — no
library, and no schedule, so the decision above still holds on its own terms. What changed is the
claim that nothing in process can fire the warm-up, which is true under scale to zero and was
written as though it were true always.

- **It runs once, as the application starts.** The first draft also took an interval and a delay,
  both reflex rather than reasoning. A start is the only moment a replica's disk is empty, and
  nothing removes an index file underneath a running replica — deleting a project is the exception
  and is meant to leave it cold, and a refresh replaces the file rather than removing it — so the
  interval guarded against nothing. The delay only postponed work the operator asked for. What holds
  the warm-up off the startup path is a `Task.Yield`, not a clock: the server begins listening while
  the projects restore behind it.
- **It is off unless `Refresh:WarmUpOnStart` is set**, and absent configuration therefore keeps the
  behaviour #9 shipped.
- **It cannot replace the Container Apps Job.** A hosted service runs only while the container does.
  Under scale to zero the call that has to happen before working hours is also the call that wakes
  the container, and nothing inside a stopped container can make it. The endpoint stays, and a cron
  is still how a scale-to-zero deployment warms up.
- **Switching it on under scale to zero is worse than leaving it off.** Wakes are frequent there, and
  warming every project on each one is exactly the cost lazy attach was built to avoid: an off-hours
  wake would pay for everyone's restore rather than one project's. It is for `minReplicas` at one.

## Consequences

- The `CodeSearch` web components do not port directly: they use `react-router-dom` and a global
  stylesheet, both replaced here.
- Ticket #2's walking skeleton reads projects from `control.duckdb` rather than `appsettings`, so
  the control database and a minimal project-management endpoint arrive with the skeleton.
- Two dependencies are young: ModelContextProtocol 2.x and `@tanstack/highlight` 0.1.0. A breaking
  change in either is absorbed by the ticket that meets it and recorded here.
- RE2 syntax is a product decision as much as a library one. An agent that writes a .NET-style
  pattern gets an explanation, never an empty result (CODING_STANDARDS, Errors).
