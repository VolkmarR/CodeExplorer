# One Shape for a Skipped File Across Declarations and Imports

The HTTP answers for a skipped file (binary or over-size) report it in two shapes, and that stays:

- `/file/declarations` has a coverage value, `Skipped`, and a `skipReason`.
- `/file/imports` keeps its `profiled` and `hasImports` flags, which describe the file's extension,
  and adds a `skipReason` that a reader checks first. For an over-size `.cs` file, the answer can say
  `profiled: true, hasImports: true` together with a `skipReason`.

## Why this is out of scope

**The answer is still correct for a reader who follows the contract.** Both doc comments, in C# and in
TypeScript, say `skipReason` comes first. The web client reads it first, and the file rail never asks
about a skipped file at all. The MCP replies are plain text and already say "not indexed".

**The fix would cost more than the inconsistency.** The options considered were:

- **A.** Give imports a coverage value matching declarations, and drop the two flags. That changes
  the HTTP answer, the API contract snapshot, `HttpJsonTests` and the web client.
- **B.** Set the flags to false for a skipped file. That swaps one misleading answer for another,
  because `profiled: false` on a `.cs` file is untrue too.

The maintainer judged a briefly misleading flag not worth either change.

## What would reopen it

A client other than the web that reads `/file/imports` and acts on the flags without checking
`skipReason`. Option A is the change to make then.

## Prior requests

- #380: "Declarations and imports mark a skipped file in two different shapes"
