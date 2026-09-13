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
- **OpenTelemetry 1.18.0**: `Exporter.OpenTelemetryProtocol`, `Extensions.Hosting`,
  `Instrumentation.AspNetCore`, `Instrumentation.Http`, `Instrumentation.Runtime`. OTLP as in the
  proof of concept, vendor-neutral; telemetry is off when no endpoint is configured.
- **No scheduler library.** The app scales to zero, so an in-process timer cannot fire the warm-up
  or a scheduled refresh. The API exposes operator endpoints for refresh and warm-up and a
  Container Apps Job on a cron calls them. Progress reaches the web UI by polling a status
  endpoint.
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

## Local development stays complete without Azure

Every Azure service above is selected by the presence of its configuration, and its absence selects
a local default: a folder on disk in place of Blob Storage (Parquet, the control database backup,
the key ring), the framework's default Data Protection key ring, authentication off, no telemetry
exporter, and `INSTALL fts` at runtime with the substring-scan fallback when offline. A plain
`dotnet run` with an empty `appsettings` is therefore a complete, offline, unauthenticated server,
and a ticket that breaks that has a defect.

## Web UI

- **React 19.3, Vite 8.3, TypeScript 7.0, pnpm**, built into `CodeExplorer/wwwroot`.
- **TanStack Router 1.170** (`@tanstack/react-router` + `@tanstack/router-plugin`): file-based
  routes, typed search params via `validateSearch`. Shareable state lives in the URL through the
  router's search params; there is no state library.
- **Tailwind CSS 4.3** (`@tailwindcss/vite`) with **shadcn 4.21** components. shadcn copies
  component source into the repo; those files are ours to edit and are not reinstalled over local
  changes.
- **@tanstack/highlight 0.1.0** for the file view, with a **custom C# language definition** written
  in this repo. Chosen knowing that 0.1.0 ships no C# and has no grammar engine; the definition is
  hand-written patterns and a candidate for upstream contribution.
- **oxlint** as the linter, carried over from the proof of concept.

## Consequences

- The `CodeSearch` web components do not port directly: they use `react-router-dom` and a global
  stylesheet, both replaced here.
- Ticket #2's walking skeleton reads projects from `control.duckdb` rather than `appsettings`, so
  the control database and a minimal project-management endpoint arrive with the skeleton.
- Two dependencies are young: ModelContextProtocol 2.x and `@tanstack/highlight` 0.1.0. A breaking
  change in either is absorbed by the ticket that meets it and recorded here.
- RE2 syntax is a product decision as much as a library one. An agent that writes a .NET-style
  pattern gets an explanation, never an empty result (CODING_STANDARDS, Errors).
