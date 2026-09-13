# CodeExplorer

A server that clones git repos and exposes the code to coding agents with a remote MCP server.

## Coding standards

`CODING_STANDARDS.md` holds the rules tooling cannot check — layout, testing, error shape,
cancellation, telemetry and comments. Formatting and style are enforced by `.editorconfig`.

## Agent skills

### Issue tracker

Issues live as GitHub issues in `VolkmarR/CodeExplorer`, managed with the `gh` CLI. See `docs/agents/issue-tracker.md`.

### Triage labels

The five canonical triage roles, each label string equal to its name. See `docs/agents/triage-labels.md`.

### Domain docs

Single-context: `CONTEXT.md` and `docs/adr/` at the repo root. See `docs/agents/domain.md`.
