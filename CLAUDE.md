# CodeExplorer

A server that clones git repos and exposes the code to coding agents with a remote MCP server.

## Coding standards

`CODING_STANDARDS.md` holds the rules tooling cannot check — layout, testing, performance, error
shape, cancellation, telemetry and comments. Formatting and style are enforced by `.editorconfig`.
Read its Performance section before any performance change: it is proven by a before/after run
with the query-plan switch (`CODEEXPLORER_EXPLAIN_DIR`).

## Agent skills

### Issue tracker

Issues live as GitHub issues in `VolkmarR/CodeExplorer`, managed with the `gh` CLI. See `docs/agents/issue-tracker.md`.

### Triage labels

The five canonical triage roles, each label string equal to its name. See `docs/agents/triage-labels.md`.

### Domain docs

Single-context: `CONTEXT.md` and `docs/adr/` at the repo root. See `docs/agents/domain.md`.
