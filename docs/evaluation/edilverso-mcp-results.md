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
