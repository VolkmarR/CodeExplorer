# CodeExplorer

A server that clones git repos and exposes the code to coding agents with a remote MCP server.

An agent connects to one project's endpoint and searches and reads that project's code. An operator
creates the projects, points them at repositories, and builds their indexes from the web UI.

## Running it

```
dotnet run --project CodeExplorer     # API, MCP and the built UI on http://localhost:5080
```

No configuration is needed: absent settings select local defaults, so a plain `dotnet run` is a
complete, offline, unauthenticated server (ADR-0004). Traces and metrics are one of those defaults:
set `Telemetry:OtlpEndpoint` (or the standard `OTEL_EXPORTER_OTLP_ENDPOINT`) to export them, and
leave it unset to run with no exporter at all.

The durable copy is another of those defaults: with no `Storage:BlobContainerUrl` set, every
project's Parquet export and the control database's backup go to a folder under `data/durable`
instead of Blob Storage, and the app needs no Azure at all.

The Data Protection key ring is the third: with no `Storage:BlobContainerUrl` set it stays the
framework's default, a folder under the user profile, which is correct on a developer machine and
wrong in a container — the disk is wiped on every stop, so a new key ring comes up each time and
every stored repository credential becomes undecryptable. Set that container and the key ring is
persisted to `keyring/keys.xml` in it; set `Storage:KeyVaultKeyUrl` as well and the keys are wrapped
by that Key Vault key, which is the deployed shape. The server says which of the three it came up in
as it starts, and warns about the two that are not the deployed one. Pointing a machine that has been
running locally at a container is a one-way step: a persisted key ring is read under a fixed
application name rather than the framework's path-derived one, so credentials stored before the
switch have to be set again.

Authentication is the fourth, and the one that changes what the server *is*: with no
`AzureAd:ClientId` set there is no tenant to check anything against, so every endpoint answers
anonymously and local work stays a single `dotnet run`. Set `AzureAd:ClientId` and `AzureAd:TenantId`
(plus `AzureAd:Instance` for a cloud other than the public one, and `AzureAd:ClientSecret` for the
web UI's sign-in) and every endpoint requires an authenticated caller by default: an API client or an
MCP client presents a bearer token, and the browser signs in through this server and holds a cookie,
never a token. A client id with no tenant id stops the server rather than coming up half-configured,
and the server says at start which of the two shapes it is in.

Three things stay anonymous on purpose, and they are the whole list: the sign-in endpoints, the
per-project protected-resource documents below, and the UI's own static bundle. The last of those is
not a policy decision but a consequence of how it is served — `Program.cs` says why, and what is
open is the bundle and nothing it renders, because every byte of that comes from `/api`.

An MCP client discovers the tenant from the 401 itself: a project endpoint answers
`WWW-Authenticate: Bearer resource_metadata="…/.well-known/oauth-protected-resource/projects/{slug}/mcp"`,
and that document — anonymous, one per project, a 404 for a slug that is no project — names the
authority to sign in against. There is deliberately no per-project authorization: every project
shares one audience, so any authenticated user reaches every project.

The sign-in cookie is protected by the same Data Protection key ring as the credentials above, which
is why it survives a restart only where that key ring does. A container without
`Storage:BlobContainerUrl` signs every operator out on every stop.

The Azure half of that is not covered by the tests — they assert that a configured deployment gets a
blob repository and a Key Vault encryptor, and nothing reaches an account. To check it for real:
point both settings at a container and a key the signed-in identity may use, start the server, add a
repository with a credential, confirm `keyring/keys.xml` appeared in the container, then restart and
refresh that repository — a clone that authenticates is the key ring having survived.

Projects attach on first connection rather than at startup, so a replica waking with an empty disk
restores the one project being connected to. `POST /api/warmup` walks every project and restores
each; an external cron calls it before working hours, because a stopped container has nothing
running to fire a timer and that call is also what wakes it.

A replica you keep running can warm itself instead: set `Refresh:WarmUpOnStart` and every project is
restored once, as the application starts, in the background while the server is already answering.
It is off by default and is not a replacement for the cron — under scale to zero, warming every
project on every wake is the cost attaching lazily exists to avoid.

The web UI is served from `CodeExplorer/wwwroot`, which `web/` builds into. To work on it with hot
reload, run `vp dev` in `web/` alongside the server and use <http://localhost:5173>; see
[`web/README.md`](web/README.md).

## Running it in a container

```
docker build -t codeexplorer .
docker run -p 8080:8080 codeexplorer
```

One build from a clean checkout: the `Dockerfile` builds the UI, publishes the API and serves both.
It runs as the non-root `app` user, contains no `git` binary — LibGit2Sharp bundles libgit2 — and
carries the DuckDB `fts` extension, installed during the build so that nothing reaches the network
for it at runtime. That last one is the reason the image exists at all. `INSTALL fts` fails silently
when it cannot reach out, and the replica then answers every search by substring scan: not an error,
just different rankings, on every project at once (ADR-0004). The image therefore also sets
`Index:SearchEngine` to `Fts`, so an extension that ever went missing from it stops the replica at
startup instead of quietly reordering results. Off the image that setting stays absent, where `Auto`
and its substring fallback are what make a plain `dotnet run` work offline.

Two paths are settings rather than fixed, because both are on the 8 GiB ephemeral disk ADR-0003
budgets and an operator has to be able to watch and move them:

| Setting                    | In the image         | What lives there                                            |
| -------------------------- | -------------------- | ----------------------------------------------------------- |
| `Storage:DataDirectory`    | `/data`              | Clones, project indexes, scratch Parquet, control database. |
| `Index:ExtensionDirectory` | `/duckdb/extensions` | The `fts` extension the build installed.                    |

DuckDB's spill files have no setting of their own and need none: it spills beside the database file,
which is the shared instance catalog under `indexes/`, so moving the data directory moves the spill
with everything else.

In a container, set `Storage:BlobContainerUrl` as well. The disk is wiped on every stop, so without
it the Data Protection key ring comes up new each time and every stored repository credential — and
every operator's sign-in cookie — stops decrypting. The section above says what else that unlocks.

Outside the image, `Index:ExtensionDirectory` is best left unset: DuckDB then uses its own folder
under the user profile, which is shared with every other DuckDB on the machine and is why a
developer downloads the extension once rather than once per checkout.

## Layout

| Path                 | What it is                                                        |
| -------------------- | ----------------------------------------------------------------- |
| `CodeExplorer/`      | The host: endpoints, storage, MCP tools. One folder per module.    |
| `CodeExplorer.Tests/`| xunit.v3 against a real DuckDB, mirroring those folders.           |
| `web/`               | The operator UI. Outside the solution; builds into `wwwroot`.      |
| `docs/adr/`          | The decisions the code follows from.                               |
| `Dockerfile`         | UI, API and the baked `fts` extension in one build.                |

`CONTEXT.md` holds the vocabulary; `CODING_STANDARDS.md` holds the rules tooling cannot check.
