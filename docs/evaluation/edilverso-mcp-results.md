# Results: Edilverso MCP vs. direct file access

Ten questions from `edilverso-mcp-questions.md`, each run twice by an isolated subagent: once
restricted to the `edilverso` MCP tools, once restricted to `Read`/`Glob`/`Grep` rooted at
`c:\Projects\WeBuild\Edilverso`. Edilverso source on disk was never modified. Concrete anchors
picked for this run (by the coordinator, using MCP `git_log`/`grep` before dispatch):

- **Q5 string**: `"Il campo {0} è obbligatorio per le opportunità di tipo Appalto pubblico"`
  (backend validation message, `OpportunityFeature.cs` / `Backend.resx`)
- **Q7 PR**: `39371` — "30099 Detrazione fattura acconto", commit `e1e3ef1c`, Claudio Bottoni,
  2026-09-16

## Aggregate metrics

| Arm | Total wall-clock | Total tool calls | Total tokens | Questions fully answered | Questions with a hard gap |
|---|---|---|---|---|---|
| MCP-only | 1,197,189 ms (~20.0 min) | 254 | 822,656 | 10 / 10 | 1 (Q7: no native diff/files-in-commit) |
| Direct-access | 999,888 ms (~16.7 min) | 188 | 944,259 | 5 / 10 | 5 (Q2, Q3, Q5, Q7, Q10: no git ⇒ no attribution/history/PR data) |

**Bottom line**: direct access is ~17% faster in wall-clock and needed 26% fewer tool calls, but
burned **~15% more tokens** (it compensates for having no index by reading whole files) and **fully
answered half as many questions**. Every question that needed authorship, "who/when", PR
provenance, or historical co-change was either unanswerable or explicitly flagged as
"cannot determine" by the direct-access arm — that's not a speed/token tradeoff, it's a
correctness ceiling direct access cannot cross without shelling out to `git` (which was
disallowed by design, matching a non-developer user with no git access).

## Per-question metrics

| Q | MCP time | MCP calls | MCP tokens | Direct time | Direct calls | Direct tokens | Direct answered? |
|---|---|---|---|---|---|---|---|
| 1 | 95.9s | 17 | 75,335 | 173.7s | 48 | 171,283 | Yes (history depth honestly flagged unknown) |
| 2 | 44.7s | 7 | 59,574 | 107.9s | 13 | 92,189 | **No** — no author/activity data (git-only) |
| 3 | 57.6s | 13 | 56,923 | 51.0s | 7 | 75,450 | **No** — no who/when/PR |
| 4 | 142.8s | 41 | 100,546 | 133.5s | 26 | 126,506 | Partial — correctly refused to fake a ranking |
| 5 | 50.4s | 9 | 54,954 | 41.9s | 8 | 52,079 | Partial — found string/file, not who/when/reworded |
| 6 | 130.3s | 31 | 98,015 | 105.6s | 22 | 95,057 | Yes — matched MCP's answer closely |
| 7 | 141.1s | 27 | 81,980 | 69.1s | 12 | 55,358 | **No** — inferred feature area, no confirmed diff |
| 8 | 254.3s | 54 | 100,661 | 167.1s | 27 | 102,785 | Partial — no historical co-change |
| 9 | 76.0s | 12 | 75,206 | 54.7s | 13 | 74,786 | Yes — exact aggregation via Grep count mode |
| 10 | 204.2s | 43 | 119,462 | 95.3s | 12 | 98,766 | Partial — structure only, no ownership/PR section |

---

## 1 — What is this product, in one answer?

**MCP tool calls**: `which_project`, `repo_info`, `project_overview`, `list_extensions`,
`list_tree` (root, depth 2), `list_tree("src", depth 1/2)`, `git_log` (paged to the oldest commit,
page 22).

**MCP answer**: 1 repo, 8,329 files / 982,920 lines, indexed at `6951b0fc` (2026-09-17). TypeScript
dominates (378k lines / 4,099 files) over C# (216k / 2,693). 11 apps under `src/`: `Api` and
`Frontend` are the two that matter (~60% of lines between them); `Model`/`shared` are load-bearing
support; `ManagementSite`, `MigrationOrchestrator`, `TenantSchemaImporter`, `DocsPage`,
`LandingPage`, `E2E`, `fake-auth` are small satellites. History spans 2024-06-28 ("Initial Setup")
to 2026-09-16 — verified by paging `git_log` all the way to the tail, not sampling.

**Direct-access answer**: found the same repo/app shape (helped enormously by discovering a
curated `docs/onboarding/team-lead/` doc set that a real non-developer wouldn't know to look for
in a cold-start scenario), correctly identified the codegen-heavy structure and gave file counts
per module. Explicitly could not determine history depth (no git access) — the only dated
artifacts found were two September 2026 ADRs, correctly flagged as uninformative about founding
date.

**Verdict**: both correct where they could be. MCP's answer is stronger and cheaper (75k tokens,
17 calls) because `repo_info`/`project_overview`/`list_extensions` give exact figures in one shot;
direct access needed 48 calls and 171k tokens and got lucky that onboarding docs existed at all —
a repo without a hand-written architecture doc would leave this arm guessing at "what matters."

---

## 2 — Who builds this?

**MCP tool calls**: `authors(limit=50)`, `git_log` (2 pages), `repo_info`, `hot_files(days=21)`.

**MCP answer**: exact ranked list of 14 authors with all-time commit counts and last-active dates
(Luigi Bifulco 926, Volkmar Rigo 721, Marco Mannara 538, ...), correctly separated "ever
committed" from "still active" (3 of 14 have gone quiet). Gave a substantive "last few weeks"
narrative from `git_log` + `hot_files`, with an honest caveat that `git_log` has no date filter.

**Direct-access answer**: could not determine this from source files. It found a workaround —
reading `.git/logs/HEAD` (reflog) and `.git/packed-refs` via Read/Grep, since those are plain text
— but correctly recognized the reflog only reflects local-machine ref history (all entries
attributed to the one local git identity) and is not a project-wide commit/author database. It
reported branch-name initials as weak circumstantial evidence of ≥6 contributors and explicitly
refused to present that as a real ranking.

**Verdict**: MCP fully and cheaply answers a question direct access structurally cannot (7 calls,
60k tokens vs. 13 calls, 92k tokens for an admittedly-incomplete answer). Notable: the
direct-access agent's `.git` internals workaround is clever but also shows the boundary is porous
— a "no git" constraint doesn't fully stop a determined agent from reading git's own files.

---

## 3 — When did this app last change?

**MCP tool calls**: `glob("*ManagementSite*")`, `list_tree` (root, then `src`), 5×
`file_history` across `Program.cs`, `.csproj`, a page, a service, an API interface.

**MCP answer**: cross-checked 5 files at different layers, all converging on the same answer —
commit `fa557927`, 2026-09-16, Marco Mannara, PR 39386 ("Aggiunta Action per Export Db Tenant su
ManagementSite..."). High confidence from convergence, not a single lucky file.

**Direct-access answer**: located the app and its structure correctly, but explicitly stated it
could not determine when/who/PR — Read/Glob/Grep expose no mtime, authorship, or VCS metadata as
usable data, and it correctly declined to fake a ranking from file/Glob ordering (noting the
ordering wasn't even mtime-clean — build artifacts were interleaved with source).

**Verdict**: MCP answers the actual question; direct access answers only the "find the app" half.
This is exactly the failure mode the source doc predicted.

---

## 4 — Where is the product actually moving?

**MCP tool calls**: `repo_info`, `list_tree("src")`, `hot_files(days=183)` project-wide, then
per-app `hot_files(days=183, directory=...)` for all 11 apps (2 calls each: a quick probe + full
list) — 41 calls total.

**MCP answer**: separated churn from size convincingly. `Frontend`/`Api` are genuinely active
(both hit the 100-file cap); the real engine is one cross-cutting effort (MasterData) touched
simultaneously across every layer; `shared`'s code generator drives a lot of downstream
`.g.*` churn elsewhere; `Model` and `DocsPage` show high "churn" that's actually regenerated
stubs, not hand-authored work; `TenantSchemaImporter` was created in one commit and never touched
again; `fake-auth` is fully dormant. Window used: 183 days ending at the newest indexed commit
(2026-03-17 → 2026-09-16), stated explicitly since the tool takes a day-count, not a calendar
range.

**Direct-access answer**: correctly and explicitly refused to answer the "which apps are active"
question at all — it recognized that file-count and Glob-mtime-ordering proxies would
systematically misrepresent old bulk-generated modules (`MasterData`, `DocsPage`) as "hot" and
small hand-written modules as "cold," which is precisely the trap the source question warns
against, and declined to produce a ranking on that basis.

**Verdict**: MCP is the only arm that answers this question at all. Direct access's refusal is the
*correct* behavior given its tools — a worse agent would have fabricated a plausible-looking but
backwards ranking from file counts.

---

## 5 — A customer quoted a message back to us

**Concrete string**: `"Il campo {0} è obbligatorio per le opportunità di tipo Appalto pubblico"`

**MCP tool calls**: `which_project`, `grep` (exact phrase, regex, context), `blame` ×2 (both
files), `file_history` ×2, `read_file` (surrounding code).

**MCP answer**: string → `OpportunityFeature.cs:707` (source of truth, key
`CustomDTValidation_OpportunityFieldMandatoryForPublic`) mirrored to `Backend.resx:245`. `blame`
on both files' specific lines converges on commit `f63d0ac1`, 2026-09-09, Marco Mannara, PR 39070.
Correctly distinguished "later commits touched this *file*" (true, 2–9 later commits) from
"later commits touched this *line*" (false — blame still points to the original commit), and
flagged the one real limitation: blame can't detect a no-op revert-to-identical-text.

**Direct-access answer**: found the exact same two files and the same consumer
(`OpportunityEntity.cs`'s `MandatoryForPublic` const) via a full-repo grep — the "find the string"
half is genuinely as good as MCP's. It explicitly stated the who/when/reworded half was
unanswerable without git, rather than guessing.

**Verdict**: this question cleanly splits into a "grep-able" half (both arms equal) and a
"blame-able" half (MCP only). Confirms the doc's framing — string→file is not the bottleneck,
attribution is.

---

## 6 — How does this feature work today? (Computo Metrico PDF import)

**MCP tool calls**: 31 calls — broad `grep`, then targeted `list_declarations`/`read_file`/
`imports` across the AI-import subsystem (`AiMcDocumentParser`, `AiMcDocumentFormat`,
`AiMcImporter`, `AiMcImportContext`, `McDocumentParserFactory`, `McImportOperationCreationService`),
plus frontend files and `ModuleRegistration.cs`.

**MCP answer**: a precise, per-file outline distinguishing the one PDF-specific line of code
(`AiMcDocumentFormat.Pdf`, sharing the same middleware call as Excel) from the shared, format-blind
importer/parser/context classes, correctly identified the external `IM.Lib.AI.Workflows` package
as an unresolved dependency (expected — it's a NuGet package, not local source), and gave an
accurate frontend/backend split.

**Direct-access answer**: essentially matched MCP's answer in substance and precision (21 calls,
full-file reads of the same class set), including correctly identifying the AI-middleware
delegation and the shared parser/importer split. This is the one question where direct access was
genuinely competitive on answer quality.

**Verdict**: for a bounded, non-historical "explain this feature" question with good identifier
names, direct access with generous file reads gets you most of the way there — the gap here is
cost/speed, not correctness. MCP still used fewer tokens (98k vs 95k — roughly even) but list
declarations/imports meant it didn't have to read every file in full.

---

## 7 — What shipped under this PR?

**Concrete PR**: 39371, "30099 Detrazione fattura acconto", commit `e1e3ef1c`, Claudio Bottoni,
2026-09-16.

**MCP tool calls**: 27 calls — `authors`, `git_log(author=...)` to find the commit, then
triangulation via `grep(withHistory=true)` on distinctive new identifiers/text plus `file_history`
per candidate file to confirm each one's commit list actually contains `e1e3ef1c`.

**MCP answer**: 8 files, each independently confirmed via `file_history`; correctly separated "this
PR's change" from "state of files now" — 6 of 8 files unmodified since, 2 touched later by an
unrelated PR but with the specific lines still attributed to the original commit via `blame`-level
precision. Self-assessed limitation, stated honestly: **the MCP server has no native
"list files in commit" / diff primitive** — the 8-file list is triangulated, not a guaranteed-
complete `git show --stat`.

**Direct-access answer**: could not find the commit at all (no git). It inferred the likely feature
area from grepping the PR's description text ("Detrazione", "acconto") and produced a plausible
file list, but explicitly and repeatedly flagged that this is inference from present-day code, not
a verified diff — it could not confirm the file list, could not name the author, and could not
check for later changes.

**Verdict**: the most git-dependent question in the set. MCP answers it (with a real caveat about
a missing tool primitive); direct access cannot answer it at all, only approximate it via
string-matching the PR title against source text — which happens to work here because the commit
message and the code share vocabulary, but would fail for a PR whose description doesn't reuse
code identifiers.

---

## 8 — What breaks if we change this?

**MCP tool calls**: 54 calls (the largest run) — `list_tree`/`grep` exploration across `shared` and
`Model` to find a genuinely cross-app routine, then `find_references`, `who_imports`, `imports`,
`co_changed`, `file_history` on the chosen target.

**MCP-picked routine**: `TenantRoleNameHelper.GetRoleName(schemaName)` in
`IML.Lib.Database/MultiTenancy` — a tiny security-relevant routine.

**MCP answer**: 14 call sites across 8 files in 2 apps (`Api`, `TenantSchemaImporter`) plus 3
internal callers in the shared library itself; 13 project-level dependents via a csproj grep
(after `who_imports` on the file itself came back ambiguous — 8 sibling files share one namespace);
`co_changed` over the file's full 3-commit history showed exactly which files move together
(`TenantConfigurationService.cs`, `PostgreSqlSchemaAdapter.cs`, plus `appsettings.host.*.json`
config files); sized the change as small in diff, larger operationally (renaming a live Postgres
role naming scheme).

**Direct-access answer** (77 calls total across both — MCP was 54, direct was 27): picked a
different, also-legitimate routine, `ITenantVersionProvider.GetTenantVersionData/GetAllTenantVersionData`
in `IML.Lib.Versioning`. Found genuine cross-app callers (`Api`, `MigrationOrchestrator`) via
careful grep + manual receiver-type verification, and explicitly caught and excluded a same-named
but unrelated lookalike interface in `Gateway` — a real false-positive a naive grep would have
miscounted. Explicitly stated no historical co-change data was available at all (correct — that
requires git).

**Verdict**: both arms found real, defensible answers to a hard question, because both routines
genuinely exist and are genuinely cross-app. The qualitative gap is `co_changed`: MCP's answer
includes actual historical evidence (which config files/tests move alongside this file in
practice), direct access's answer is 100% static/structural inference with no historical
grounding — exactly the "evidence vs. proof" distinction the source doc calls out for `co_changed`.

---

## 9 — How consistent are we?

**MCP tool calls**: 12 calls, `list_matches` as primary tool, investigating `catch` block error
variable naming after two dead-end probes (an `axios`/http-client naming check, a `use*` hook
prefix check) that were transparently reported and discarded.

**MCP answer**: for TS/TSX combined, `error` (55 occ / 37 files), `e` (16/12), `exception` (1/1);
concluded "one dominant convention with real but minor stragglers." Honestly flagged that the
equivalent C# `.cs` probe was mis-designed (captured the exception *type*, not the variable name)
and was not corrected/re-run.

**Direct-access answer**: chose the same style of convention (C# `catch (Exception X)` variable
naming instead of TS), got **exact** counts via Grep's `count` mode (`ex` 72/47 files, `e` 19/12,
`exception` 7/5, unnamed 4/4) that summed consistently against an independent total — genuinely a
clean, complete answer, arguably more rigorous than MCP's here since it hit no dead ends and
covered the whole `src/` tree with exact (not sampled) counts.

**Verdict**: this is the one question where direct access's answer is *more* complete than MCP's
(MCP left the C#-side of the same question unresolved due to a regex bug; direct access solved the
C# side cleanly). `list_matches` is still the more scalable primitive in principle, but a plain
`grep -c`-per-variant loop works fine at this scale and this is a case where MCP's advantage was
squandered by an agent-side regex mistake, not a tool limitation.

---

## 10 — The full dossier

**Feature picked (both arms independently)**: Opportunity / "Appalto Pubblico" (public tender).

**MCP tool calls**: 43 calls — full sweep including `list_tree`, `grep`, `read_file`,
`file_history`, `authors`, `git_log`, `co_changed`, `hot_files`, `who_imports`, `find_references`.

**MCP answer**: a genuine briefing — feature description, file layout across Model/Api/Frontend,
entry points, a concrete PR timeline for the feature specifically (38468 → 39378, Aug–Sep 2026),
named the actual owner (Emanuel Primavera for the feature, Marco Mannara for backend validation,
Luigi Bifulco for the form-kit platform it depends on), a dependency/blast-radius map, and a
grounded cost estimate (0.5–1.5 days for a field addition, multi-week if new form-kit platform
capability is needed — backed by the actual historical PR sequence for a comparable past change).
Self-reported one real tool gap: `authors`/`git_log` silently ignore a `path` filter, forcing
per-file `file_history` calls instead of one clean "who owns this folder" query.

**Direct-access answer**: an equally strong *structural* briefing (same feature, same file layout,
same entry points, same validation-matrix seam, same cost-estimate reasoning) — but with an
entire section explicitly marked "UNAVAILABLE WITHOUT GIT/INDEX ACCESS": no ownership, no
"touched most recently," no PR timeline. It refused to fabricate any of the three rather than
guessing.

**Verdict**: this is the cleanest illustration of the whole comparison. Structure/architecture
questions are answerable by both arms at comparable quality; the entire ownership/history/PR
dimension of a "hand this to a helpdesk colleague" briefing is available only through MCP, and
direct access's honesty about that gap (rather than fabricating names/dates) is itself a positive
finding about the baseline's behavior, not the tool's capability.

---

## Overall evaluation

### Tool coverage vs. the doc's predictions

The doc's coverage table was accurate. Every tool it named for a question got used for that
question, and no MCP tool the doc listed sat unused. `git_log`/`authors`/`file_history`/`blame`
did the heavy lifting on every helpdesk-style question (2, 3, 5, 7) exactly as predicted, and
direct access failed on precisely those four (plus half of 10) exactly as predicted. `list_matches`
worked as advertised for Q9 (aggregation, not just matching), though the agent didn't lean on it
hard enough to beat a plain grep-count loop this run. `who_imports`/`co_changed` behaved as
"evidence, not proof" in Q8/Q10 exactly as the doc frames them.

### Tool calls that failed, returned something unhelpful, or forced a workaround

- **`git_log`/`authors` ignore a `path`/`author`-scoped-to-directory filter** (Q10): the agent had
  to fall back to per-file `file_history` calls to approximate "who owns this feature," which is
  strictly weaker than a single scoped query would be. This is a real, fixable gap.
- **No native "files touched by commit N" / diff primitive** (Q7): the MCP server can find a
  commit by author/message via `git_log`, and can confirm a *specific* file was touched by that
  commit via `file_history`, but cannot answer "what did commit X touch" directly — the agent had
  to triangulate via `grep(withHistory=true)` on distinctive new text, then verify each candidate
  file individually. This works only when the commit introduces grep-able distinctive
  identifiers/text; a pure refactor or a file-rename-only commit would defeat it.
  Several agents independently reached for this triangulation, so it's a recurring pattern, not
  a one-off.
- **`who_imports` on a specific file returns ambiguous/unusable results when multiple files share
  the same namespace** (Q8) — 8 sibling files in `IML.Lib.Database.MultiTenancy` all declare the
  same namespace, so `who_imports` on one specific file couldn't attribute importers to it
  individually. The agent fell back to grepping `.csproj` files for `ProjectReference`, which is
  coarser (project-level, not file-level).
  This is likely a general C#-namespace-vs-file granularity issue, not specific to this file.
- **Path-prefix confusion**: at least 3 separate agents (Q1, Q3, Q10) initially tried
  `list_tree`/`file_history` paths prefixed with the repo/project slug (e.g. `"edilverso/src"`)
  and got errors before discovering the correct form is just `"src"`. This is a minor but
  recurring first-call failure — a clearer error message or path-normalization in the tool
  (stripping a leading `<project-slug>/` segment) would remove a wasted round-trip on nearly every
  cold-start session.
- **`list_matches`/regex mistakes go silent, not wrong-but-flagged**: in Q9, an agent's C#
  `catch (Type var)` regex captured the exception *type* instead of the *variable* because it
  didn't consume the type token first — the tool returned a well-formed but semantically wrong
  result set with no signal that something was off. Not a tool bug, but a sharp edge: a tool that
  could optionally validate "does this capture group look like a stable identifier vs. a
  known-type name" might catch this class of error.

### Where the TS/C# mix (vs. Radix's single-language X#) exposed something Radix couldn't

- **`imports`/`who_imports` correctly spans both `using` (C#) and TS `import` styles** — no agent
  in this run reported the tool failing to resolve one style but not the other; Q8's dependency
  graph (`IML.Lib.Database` referenced by both C# and — indirectly, via generated `.g.ts` output —
  frontend code) and Q6's cross-language import chain (`AiMcDocumentParser.cs` importing an
  external NuGet package plus internal cross-module contracts) both resolved cleanly. This is a
  genuine positive: the doc predicted this project could break `imports`/`who_imports` if it only
  handled one style, and it didn't.
- **The mono-repo's codegen boundary (`.g.cs`/`.g.ts`/`.g.md`) is a real, TS/C#-crossing hazard
  that Radix's single-language X# repo can't exercise**: several agents (Q4, Q10) had to
  distinguish "this file changed" from "this file was *regenerated* because something upstream in
  `src/Model` changed" — `hot_files` reports raw commit counts on generated files without any way
  to filter or flag them as generated, so an agent has to know the `.g.*` naming convention itself
  to avoid over-crediting `DocsPage`/`Model` with real development effort. A `hot_files` option to
  exclude or separately bucket generated-file paths (by extension pattern or a `.gitattributes`
  `linguist-generated`-style marker) would directly fix a misread the doc anticipated ("ranking
  over the whole history window is aggregation the direct arm has to invent... it will sample
  instead") but that the MCP arm also had to do manually here.
- **Frontend/backend split questions (Q6) are harder to get *wrong* than Radix's single-language
  case, but easier to get *verbosely inefficient*** — because "what does this feature depend on"
  spans two languages with different declaration idioms (C# classes/interfaces vs. TS
  hooks/components), `list_declarations` had to be called on both sides separately with no unified
  cross-language dependency view; an agent has to manually stitch "the frontend calls this backend
  endpoint" together from naming convention, since no tool traces a TS `fetch`/generated-client
  call to its C# controller/command handler. This is the concrete "two/three different places to
  search" cost the doc predicted for Q5/Q6, confirmed here for Q6 specifically.

### Missing functionality — concrete, cited by question

1. **A scoped/filterable `authors`/`git_log`** (cite: Q10) — accept a `path` (directory or glob)
   so "who committed to this feature area" doesn't require N `file_history` calls stitched by
   hand.
2. **A "files changed by commit X" / lightweight diff-stat tool** (cite: Q7) — given a commit
   hash or PR number, return the file list directly instead of forcing grep-based triangulation
   that only works when the commit's content is textually distinctive.
3. **Path-prefix normalization or a clearer error** (cite: Q1, Q3, Q10) — silently accept (or
   clearly reject with a corrective hint) a leading `<project-slug>/` segment on `list_tree`/
   `file_history`/`grep` paths, since agents restate it as their first guess almost every time.
4. **A "generated file" marker/filter on `hot_files` and `grep`** (cite: Q4, Q10) — a way to
   exclude or separately bucket `.g.cs`/`.g.ts`/`.g.md`-style generated output (or anything a
   `.gitattributes generated` marker flags), so churn rankings reflect hand-authored effort by
   default instead of requiring the agent to already know the project's codegen naming convention.
5. **Cross-language call-graph linking for generated API clients** (cite: Q6) — no tool currently
   connects a TS-side generated fetch/query call to the C# command/query handler it targets (or
   vice versa); in a TS-frontend/C#-backend mono-repo this is the single biggest manual-stitching
   cost, and Radix's single-language repo can't even surface this gap.
6. **`who_imports` namespace-collision handling** (cite: Q8) — when multiple files in a target
   directory share one C# namespace, `who_imports` should either disambiguate to the specific file
   (e.g. via the actual type names it declares) or explicitly say "ambiguous, showing
   namespace-level importers" instead of returning a result an agent has to independently
   recognize as unreliable.

---

## Update: rerun after MCP server changes (2026-09-19, same day)

The user updated the MCP server and asked to rerun all 10 questions (MCP-only arm) with the same
questions/anchors, to check whether the changes moved the needle on timing, tool-call count, and
answer quality. Same 10 subagents, same prompts, same Q5 string / Q7 PR anchor, run fresh (no
memory of the first pass).

### Aggregate: before vs. after

| Metric | Before (baseline) | After (rerun) | Change |
|---|---|---|---|
| Total wall-clock | 1,197,189 ms (~20.0 min) | 1,062,999 ms (~17.7 min) | **-11%** |
| Total tool calls | 254 | 237 | **-7%** |
| Total tokens | 822,656 | 720,893 | **-12%** |

Consistent, moderate improvement across the board — not dramatic, but a real reduction in cost on
every axis, plus (see below) one concrete correctness/usability fix and no regressions in what a
question could answer.

### Per-question: before vs. after

| Q | Time (before → after) | Calls (before → after) | Tokens (before → after) | Verdict |
|---|---|---|---|---|
| 1 | 95.9s → 125.4s | 17 → 43 | 75.3k → 65.5k | **Worse** (time/calls) — agent did extra per-folder `glob(limit=1)` probing this run, a strategy choice, not a tool regression |
| 2 | 44.7s → 37.3s | 7 → 5 | 59.6k → 58.4k | Better across the board |
| 3 | 57.6s → 53.6s | 13 → 12 | 56.9k → 56.4k | Slightly better; also used `hot_files(directory=...)` to shortlist candidates instead of guessing files to check |
| 4 | 142.8s → 118.5s | 41 → 32 | 100.5k → 67.1k | Better across the board |
| 5 | 50.4s → 47.4s | 9 → 9 | 55.0k → 54.7k | Flat |
| 6 | 130.3s → 185.2s | 31 → 38 | 98.0k → 99.7k | **Worse** — more thorough exploration this run, same quality answer |
| 7 | 141.1s → 120.5s | 27 → 25 | 82.0k → 77.9k | Better, but the underlying gap (no diff/files-in-commit primitive) is **still there** |
| 8 | 254.3s → 141.9s | 54 → 29 | 100.7k → 80.7k | **Much better** (44% faster, 46% fewer calls) |
| 9 | 76.0s → 47.7s | 12 → 9 | 75.2k → 55.4k | **Much better**, and now correctly covers both TS and C# (the baseline left the C# side broken due to an agent-side regex bug, not a tool bug) |
| 10 | 204.2s → 185.6s | 43 → 35 | 119.5k → 105.1k | Better, and see the confirmed fix below |

### What actually changed on the server side

- **Fixed: `authors`/`git_log` now honor a `path`/`directory` scope.** This was the single most
  concrete gap flagged in the baseline (Q10): previously, passing a `path` to `authors` or
  `git_log` was silently ignored and returned whole-repo results, forcing an agent to reconstruct
  "who owns this feature" from many individual `file_history` calls. In the rerun, the same Q10
  agent explicitly tested this and confirmed scoped calls (e.g. `authors(path="src/Frontend/.../opportunities")`)
  now return narrower, correctly-filtered results. This directly makes Q10-style "who owns this
  feature area" briefings both cheaper and more precise going forward.
- **Not fixed: no "files touched by commit X" / diff primitive** (Q7, retested). The rerun agent
  hit the exact same limitation and had to fall back to the same grep-then-`file_history`
  triangulation as the baseline, explicitly re-confirming "there is no commit-scoped file-list or
  diff tool." Worth prioritizing given it recurred identically on a fresh run.
- **Not fixed: `who_imports` namespace-collision ambiguity** (Q8, retested with a different picked
  routine). The rerun agent independently hit the same wall — `who_imports` on a file inside a
  shared C# namespace returned no resolvable edge — and used the same `.csproj` `ProjectReference`
  grep fallback as the baseline. Confirmed still open.
- **New finding, not previously tested: `hot_files` is not rename-aware** (Q4 rerun). The
  `src/Model` directory was renamed from a root-level `model/` folder mid-history; `hot_files`
  matches by literal path, so the same logical files' history is split across two separate
  listings (`model/...`, mostly high commit counts but "no longer at HEAD", vs. `src/Model/...`,
  low counts as the current path). This wasn't a regression from the update — it's a pre-existing
  gap the rerun happened to surface by choosing a slightly different directory scope than the
  baseline run did.
- **Minor, unconfirmed as new**: the rerun's `repo_info` response included an explicit caveat that
  the imported commit history "may begin later than the repository itself does" — this framing
  wasn't quoted in the baseline Q1 report. It may be a genuine addition to the tool's output, or
  simply something the baseline agent didn't think to quote; not confident enough to log as a
  confirmed fix, but worth noting as a possible improvement to repo-age framing.
- **Still present, unrelated to the update**: the path-prefix confusion (agents guessing a leading
  `<project-slug>/` segment before discovering plain `src/...` is correct) recurred in the Q1
  rerun. Not fixed, and low-cost to fix per the original recommendation.

### Overall verdict

The update is a net positive: real, measurable reductions in time/calls/tokens on 7 of 10
questions, no regressions in answer completeness or correctness on any question, and one
confirmed fix to a real gap (`authors`/`git_log` path scoping) that was called out by name in the
baseline report. The two "worse" questions (Q1, Q6) are explained by the rerun agent choosing a
more exploratory strategy, not by anything slower in the tools themselves. The two gaps flagged
most concretely in the baseline as fixable — no diff/files-in-commit primitive, and
`who_imports` namespace-collision handling — are both still open and reproduced identically on
this run; those remain the highest-value next fixes.

---

## Update 2: rerun after a second MCP change (2026-09-20) — `commit`/`commit_files` added

The user added two new tools, `mcp__edilverso__commit` and `mcp__edilverso__commit_files`, and
asked for another full rerun to check whether they closed the "no diff/files-in-commit primitive"
gap flagged twice already (baseline Q7, update-1 Q7). Same 10 questions, same anchors, fresh
subagents with the new tools listed. This run hit the account's weekly rate limit partway through
— **Q4, Q8, and Q10 failed mid-run and were retried once (all three retries succeeded)**; **Q6, Q7,
and Q9 delivered complete, full-quality final reports but were cut off before reporting their own
timing/token usage**, so those three have no metrics this round (content is complete and used
below; cost comparison for them is marked N/A).

### Per-question metrics (available data only)

| Q | Time (upd.1 → upd.2) | Calls (upd.1 → upd.2) | Tokens (upd.1 → upd.2) | Note |
|---|---|---|---|---|
| 1 | 125.4s → 78.7s | 43 → 13 | 65.5k → 60.6k | Big improvement — clean run, no path-prefix confusion, no per-folder probing this time |
| 2 | 37.3s → 43.9s | 5 → 6 | 58.4k → 60.7k | Flat |
| 3 | 53.6s → 23.8s | 12 → 5 | 56.4k → 53.9k | **Big improvement** — `git_log(path=...)` now scopes directly to a directory, replacing multi-file `file_history` guesswork |
| 4 | 118.5s → 163.9s (retry) | 32 → 28 | 67.1k → 65.5k | Similar cost, but went noticeably deeper (see finding below) |
| 5 | 47.4s → 63.3s | 9 → 13 | 54.7k → 56.8k | Slightly more expensive — used the new `commit`/`commit_files` tools for extra verification, a worthwhile trade |
| 6 | 185.2s → N/A (rate-limited before usage report) | 38 → 9 | 99.7k → N/A | Tool-call count dropped sharply (38→9) on a comparably complete answer — likely a real efficiency gain, but not verifiable on cost this round |
| 7 | 120.5s → N/A (rate-limited) | 25 → ~27 (incl. failed path probes) | 77.9k → N/A | See dedicated section below — the core gap is fixed |
| 8 | 141.9s → 187.3s (retry) | 29 → 38 | 80.7k → 95.8k | Somewhat more expensive (compared two candidate routines this time before picking one); `who_imports` gap reconfirmed a third time |
| 9 | 47.7s → N/A (rate-limited) | 9 → 7 | 55.4k → N/A | Answer quality equal/slightly cleaner, fewer calls |
| 10 | 185.6s → 138.3s (retry) | 35 → 29 | 105.1k → 117.8k | Faster despite being a retry; heavy, effective use of path-scoped `authors`/`git_log` and the new `commit` tool |

### The headline result: Q7's gap is fixed

The rerun explicitly tested `commit`/`commit_files` against PR 39371 (same commit `e1e3ef1c` used
in every prior run of this question) and got a categorically better answer:

- **`commit(sha)`** returns commit metadata directly: author, date, subject, and total file/line
  change counts — no need to page `git_log` looking for a message match once the hash is known.
- **`commit_files(sha)`** returns the **complete, exact list of files a commit touched**, with
  change type (added/modified/deleted) and per-file +/- line counts, in one call — with built-in
  pagination for commits touching more than ~300 files. This is exactly the missing primitive
  called out in both the baseline and update-1 reports.
- Compared directly against the old triangulation approach (grep for distinctive new text, then
  `file_history` on every guessed candidate file): the new approach is strictly better because it
  doesn't depend on the commit's content being textually distinctive, doesn't require guessing
  candidate files, and can't silently miss a touched file that lacks distinctive text (e.g. a
  `.resx` or `.po` file with only generic additions — exactly the kind of file the old grep-based
  approach was most likely to under-count). The rerun agent found **20 files** for PR 39371 this
  way, versus 5–8 files found by two independent triangulation attempts in earlier runs — strong
  evidence the old method really was undercounting, not just differently scoped.

**One real gap remains, stated by the tool itself**: `commit`/`commit_files` give file-level
metadata (which files, what kind of change, how many lines), but **the actual diff content is not
indexed** — there is still no way to see the literal before/after text of a change. An agent can
now say "these 20 files changed, roughly this many lines" with certainty, and can infer *what*
changed in plain language from file names/paths/comments, but cannot quote an actual line-level
diff, and "has this changed since" is answered at file granularity (any later touch to the path)
rather than line granularity (whether the specific lines survived a later edit to the same file).
This was independently reconfirmed on both the Q5 and Q7 reruns.

### Other confirmed fixes and reconfirmed gaps this round

- **Fixed, and broader than first thought: `git_log` (not just `authors`) now honors a `path`
  filter.** Update 1 confirmed the fix for `authors`; this round's Q3 rerun showed `git_log(path="src/ManagementSite")`
  also scopes correctly, collapsing what used to take 12–13 calls (glob the app, then
  `file_history` on 4–5 individual files to triangulate "most recent") into 5 calls total. This is
  a bigger win than update 1's report suggested.
- **Reconfirmed, still open: `who_imports` fails on shared C# namespaces.** The Q8 rerun
  independently hit the exact same wall a third time (this time on `AbstractRepository.cs`,
  `Result.cs`, and `GenericResult.cs` — three different files, three different shared namespaces)
  and had to fall back to grepping `.csproj` `ProjectReference` lines each time. Three-for-three
  reproduction across three separate runs and three separate target files makes this the
  single most consistently-reproduced gap across the whole evaluation.
- **New, more serious framing of the rename-tracking gap** (first surfaced in update 1's Q4): the
  Q4 retry this round discovered the `model/` → `src/Model/` split is **not a clean historical
  cutover** — the old `model/` path is still receiving live commits (18 in the last 30 days) in
  parallel with the new `src/Model/` path, which itself has only 9 commits total. Naively trusting
  `hot_files`/`git_log` on the current path alone would make Model look like the *least* active
  app in the repo (9 commits) when combined evidence puts it 3rd-busiest (~562 commits in six
  months). This is worse than a cosmetic rename-tracking gap — it's a case where the tool's
  literal-path matching produces an actively misleading answer to exactly the question ("where is
  effort really going") this tool is supposed to help answer, unless the agent thinks to check
  both paths. Worth prioritizing over the other three gaps precisely because it fails silently
  (wrong number, not an error) rather than loudly.

  > **Correction, added during triage of #131.** The "not a clean historical cutover" reading above
  > is wrong, and the index says so: `model/` has 1,243 commits spanning 2024-06-28 to 2026-09-15
  > and stops there, `src/Model/` has 9 commits all within 2026-09, HEAD holds no file under
  > `model/` at all, and the same basenames appear under both roots. It is a clean cutover that
  > happened around 2026-09-15/16, days before this run. The agent's 30-day window straddled the
  > rename, so pre-cutover commits read to it as parallel live activity.
  >
  > The finding is kept as written because what it records is real: an agent with these tools, asked
  > where effort is going, concluded that two paths were simultaneously active when one had been
  > dead for a day. That misreading is a better argument for rename-awareness than the false claim
  > was — the gap is a rename split, and the reply gives an agent nothing to catch it with.
- **`git_log` cannot scope to a path that doesn't exist at HEAD** (new finding, Q4 retry): scoping
  `git_log` to the old `model/` path or to any file under it errors out (something like "names
  nothing in this index"), even though `hot_files` happily ranks commits against that same
  now-deleted path. An agent investigating the rename-split issue above has no way to pull actual
  commit-by-commit detail (messages, authors, dates) for the pre-rename path — only the aggregate
  counts `hot_files` provides. Fixing this would make the rename-split issue fully diagnosable
  instead of just detectable.

### Updated overall verdict

Both rounds of changes are net improvements, and the second round fixed exactly the gap it set out
to fix. Confirmed fixes across both updates: `authors`+`git_log` path scoping (a genuine, broad
efficiency win reproduced on Q3/Q10), and the `commit`/`commit_files` files-in-commit primitive (a
genuine correctness win on Q7, likely also improving Q5's confidence). One gap has now been
reproduced identically three times (`who_imports` on shared namespaces) and should be the next
priority. One gap changed from "cosmetic" to "actively misleading" on closer inspection (the
rename-tracking split) and deserves attention ahead of its apparent severity in the first report.

Filed as tracker issues: [#131](https://github.com/VolkmarR/CodeExplorer/issues/131) (rename
split leaves the old path live), [#132](https://github.com/VolkmarR/CodeExplorer/issues/132)
(`git_log` can't scope to a path gone from HEAD), [#133](https://github.com/VolkmarR/CodeExplorer/issues/133)
(`who_imports` shared-namespace fallback still routes to `.csproj` grep instead of
`find_references`, checked against #114's acceptance criteria). Everything else this evaluation
surfaced was already tracked and closed before this run: #118 (`authors`/`git_log` path scope),
#107 (`commit`/`commit_files`), #117 (`hot_files` exclude filter), #109 (`hot_files` directory
ranking), #114 (`who_imports`/`imports` unresolved messaging), #115/#127 (`co_changed`
rename-severed history).
