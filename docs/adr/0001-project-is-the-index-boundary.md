# A project is the index boundary

A project holds one or more repositories, and all of them are indexed together into a single index
that a search spans as one body of code. Files are named by qualified path — repository first, then
the path within it — so two repositories in a project may each contain `src/index.ts`.

We chose this over the obvious shape of one index per repository because a department's code is
usually spread across several repositories, and the questions agents ask ("where is this called
from?") cross those boundaries constantly. One index per repository would push the fan-out and the
result merging into every agent.

## Consequences

- A refresh of one repository invalidates part of a project's index, so the blast radius of
  reindexing is the project rather than the repository.
- The proof of concept in `CodeSearch` cannot be ported. Its schema has no `repo_id` on `files` or
  `lines`, and every import runs `DELETE FROM lines; DELETE FROM files; DELETE FROM repo` — it holds
  exactly one directory by construction. The MCP tool surface is worth keeping; the storage layer
  underneath it is not.
