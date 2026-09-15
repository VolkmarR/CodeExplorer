# CodeExplorer operator UI

The web UI an operator uses to create projects, add repositories, build indexes, and search and read
the code an agent sees over MCP. It also carries project discovery, which MCP has none of.

Built with Vite+ into `../CodeExplorer/wwwroot`, so one `dotnet run` serves the API and the UI
together. It stays out of `CodeExplorer.slnx`.

## Commands

Vite+ is the whole toolchain (ADR-0004), driven by the `vp` CLI. There are deliberately no
`package.json` scripts: `dev`, `build`, `check` and `test` are `vp` built-ins, and a script of the
same name would make `vp run <name>` ambiguous.

| Command          | What it does                                                 |
| ---------------- | ------------------------------------------------------------ |
| `vp install`     | Install dependencies. Run it after pulling.                  |
| `vp dev`         | Dev server on 5173, proxying `/api` to the server on 5000.   |
| `vp build`       | Production build into `../CodeExplorer/wwwroot`.             |
| `vp check --fix` | Format with oxfmt, then lint with oxlint and React Doctor.   |
| `vp test`        | Vitest. Today that is the C# and X# highlighter definitions. |

`vp` is the global CLI; `./node_modules/.bin/vp` is the project-local one and does the same thing.
Type checking is `tsc -b`, since `tsconfig.json` is a project-references file.

## Running it against the server

```
dotnet run --project ../CodeExplorer          # http://localhost:5000
vp dev                                        # http://localhost:5173
```

5000 is Kestrel's own default, so a plain `dotnet run` binds it with no configuration at all and the
proxy in `vite.config.ts` has something to name. `ASPNETCORE_URLS` overrides it — move the proxy
target with it if you do.

## Layout

```
web/
  vite.config.ts          Dev server, build, lint and format — the whole toolchain, one file
  lint.rules.ts           Which React Doctor rules are on, derived from the plugin's registry
  src/
    routes/               File-based routes; `validateSearch` types the URL state
    features/             One folder per concept, mirroring the server's modules (ADR-0005)
      projects/           The project list, a project's page, repository management
      search/             The search form and its results
      files/              Browsing the index, and the file view
    components/           Only what every feature uses, plus shadcn source in components/ui
    highlight/            The C# and X# language definitions and the highlighter they register in
    lib/                  The ky API client, the shared query-key roots, formatters
```
