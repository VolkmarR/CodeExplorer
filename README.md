# CodeExplorer

A server that clones git repos and exposes the code to coding agents with a remote MCP server.

An agent connects to one project's endpoint and searches and reads that project's code. An operator
creates the projects, points them at repositories, and builds their indexes from the web UI.

## Running it

```
dotnet run --project CodeExplorer     # API, MCP and the built UI on http://localhost:5080
```

No configuration is needed: absent settings select local defaults, so a plain `dotnet run` is a
complete, offline, unauthenticated server (ADR-0004).

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
