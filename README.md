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

Projects attach on first connection rather than at startup, so a replica waking with an empty disk
restores the one project being connected to. `POST /api/warmup` walks every project and restores
each; an external cron calls it before working hours, because a stopped container has nothing
running to fire a timer and that call is also what wakes it.

A replica you keep running can warm itself instead: set `Refresh:WarmUpOnStart` to warm every
project once per start, `Refresh:WarmUpDelaySeconds` (default 30) to let the request that woke the
container be served first, and `Refresh:WarmUpIntervalMinutes` to repeat it. It is off by default
and is not a replacement for the cron — under scale to zero, warming every project on every wake is
the cost attaching lazily exists to avoid.

The web UI is served from `CodeExplorer/wwwroot`, which `web/` builds into. To work on it with hot
reload, run `vp dev` in `web/` alongside the server and use <http://localhost:5173>; see
[`web/README.md`](web/README.md).

## Layout

| Path                 | What it is                                                        |
| -------------------- | ----------------------------------------------------------------- |
| `CodeExplorer/`      | The host: endpoints, storage, MCP tools. One folder per module.    |
| `CodeExplorer.Tests/`| xunit.v3 against a real DuckDB, mirroring those folders.           |
| `web/`               | The operator UI. Outside the solution; builds into `wwwroot`.      |
| `docs/adr/`          | The decisions the code follows from.                               |

`CONTEXT.md` holds the vocabulary; `CODING_STANDARDS.md` holds the rules tooling cannot check.
