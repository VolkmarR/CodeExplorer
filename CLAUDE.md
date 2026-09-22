# CodeExplorer

A server that clones git repos and exposes the code to coding agents with a remote MCP server.

## Coding standards

`CODING_STANDARDS.md` holds the rules tooling cannot check — layout, testing, error shape,
cancellation, telemetry and comments. Formatting and style are enforced by `.editorconfig`.

## Performance work

Prove every performance change with a **before/after** run on a real index in `CodeExplorer/data/indexes`.
A green test shows the answer stayed the same. It does not show the query got faster.

The **query-plan switch** is how you measure. It is the environment variable `CODEEXPLORER_EXPLAIN_DIR=<dir>`, read
once at server start. With it set, every index read writes `<stamp>-<File>-<Member>.sql.txt` (the statement and
its parameters) and `.json` next to it (DuckDB's profile of the real execution: `latency` at the root, and
`operator_cardinality` on every operator). Tests turn the same switch on with `QueryPlan.Recording(dir)`, since
the variable is read too early for them (`CodeExplorer/Reading/QueryPlan.cs`).

1. **Record the baseline first**, on a clean `HEAD`, before you edit anything. Build and start the server with
   the switch on, then call the affected endpoint or tool about ten times per case. Save the answers too.
2. Make the change, build, and repeat into a second directory against the same index. If a refresh ran in
   between, the baseline is stale: take it again.
3. For each case, report the median `latency` before and after, the row count at the operator the change
   targets, and whether the answers are identical.

If the change already exists and there's no baseline, stash only the source (`git stash push -- <file>`) to
take one, then pop it.

Plans taken on a customer index belong in `docs/evaluation/`, which is gitignored.

## Agent skills

### Issue tracker

Issues live as GitHub issues in `VolkmarR/CodeExplorer`, managed with the `gh` CLI. See `docs/agents/issue-tracker.md`.

### Triage labels

The five canonical triage roles, each label string equal to its name. See `docs/agents/triage-labels.md`.

### Domain docs

Single-context: `CONTEXT.md` and `docs/adr/` at the repo root. See `docs/agents/domain.md`.
