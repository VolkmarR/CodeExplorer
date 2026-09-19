# Radix MCP arm — results

Run date: 2026-09-19. MCP arm only, per `mcp-vs-direct-access.md` — no direct-access (filesystem)
arm was run in this session. Each question was answered by a fresh general-purpose subagent given
only the `mcp__radix__*` tools and the exact question text from the source doc (with a concrete
string/ticket substituted for Q5/Q7 as the doc specifies). Metrics below (`tool uses`, `duration`,
`tokens`) are the harness-reported figures for each subagent's run; "tool calls" in each section is
the subagent's own self-reported call log (occasionally one or two lower than the harness count,
since the harness count includes the final hand-back call).

Two run-level caveats that apply to every metric below:
- All 10 subagents ran **in parallel**, launched from a single batch. Wall-clock duration per
  question is each agent's own elapsed time, not affected by the others, but this coordinator could
  not observe a truly serial "MCP vs direct" timing comparison — only the MCP arm, as scoped.
- Token counts are each subagent's own context consumption, not a proxy for MCP-server cost.

---

## 1 — What is this product, in one answer?

**Question asked**: verbatim from the doc.

**Tool calls** (26 real calls + 2 schema-only loads, in order): `which_project`, `repo_info`,
`project_overview`, `list_tree(depth=2)`, `list_extensions`, `git_log(limit=5)`,
`hot_files(days=3650)`, `list_tree(radix/src)`, `list_tree(acslib/src)`, `hot_files(days=7300,
limit=1)`, then a 15-call binary search over `git_log(limit=1, page=N)` (varying `page` and `repo`)
to bound each repo's oldest commit.

**Answer given**: 2 repos (radix 40,962 files/7.85M lines, acslib 8,727 files/1.64M lines); X#
`.prg` dominant (33,341 files/5.64M lines), `.xsfrm` forms second (2.27M lines), C# a distinct but
minor layer (534K lines), `.rc` string tables largely translation data not logic; named the RX*S/W
module-per-domain pattern (Verkauf, Einkauf, Fibu, Basis, CRM, etc.) and RadixDN_Exe/RadixDE-EN-IT;
history goes back to "at least 2021 for radix, 2022 for acslib," with an explicit caveat that the
indexed history may not be the project's true origin (likely pre-dates a VCS migration).

**Metrics**: 150.9s wall clock · 28 tool uses · 66.2K tokens. Reached in full — no truncation. No
confidently-wrong claims found; every uncertain claim (module semantics from German names, "history
may predate 2021") was explicitly flagged as inference rather than asserted as fact.

**Gap surfaced**: `git_log` has no "how many total commits" / "what's the oldest commit" mode. The
agent had to binary-search `page × limit=1` — 15 of its 28 tool calls — just to bound each repo's
start date to within a few hundred commits. This alone accounts for over half its tool-call budget.

---

## 2 — Who builds this?

**Question asked**: verbatim from the doc.

**Tool calls**: `repo_info`, `authors(limit=30)`, `git_log(limit=50)`, `hot_files(days=30,
limit=20)`, `git_log(repo=acslib, limit=10)`.

**Answer given**: full 24-person contributor list ranked by lifetime commit count with exact
counts; identified the ~8-10 people currently active by cross-referencing last-commit date against
frequency in the 50 most recent commits (Matthias Mur, Hansjoerg Petriffer, Peter Ferdigg, Antonio
Nigro, Abimanyu Ravi, Patrick Kaltenhauser, Denis Pavani, Daniel Psenner, plus Holger Steinmair,
Johannes Feichter, Stefano Gennarelli on a smaller scale); correctly flagged several high-lifetime-
count authors (Marcel Caldato, Volkmar Rigo, Alex Sigmund, others) as no longer active; summarized
"last few weeks" work by theme (warehouse/inventory, sales/purchasing docs, e-invoicing, DB schema
bump, AI/feature-flag work) drawn from real commit messages and hot_files.

**Metrics**: 53.2s wall clock · 7 tool uses · 56.5K tokens. Fastest and cheapest of all 10 runs.

**Self-reported gaps**: "most active" is a synthesis (last-commit-date × eyeballed frequency in a
50-commit sample), not a single tool-verified number — radix has no direct "commits per author in
window N" metric. The agent explicitly declined to treat its "roughly a third" estimate for
Matthias Mur as precise. `git_log`'s 50-commit page for `radix` only reached back 10 days (to
2026-09-08); it did not page further to fully cover "the last few weeks," relying on the 30-day
`hot_files` window to corroborate instead. No confidently-wrong claims.

---

## 3 — When did this module last change?

**Question asked**: verbatim from the doc (with the "RX…" naming convention given as background,
since the doc's own text supplies that hint).

**Tool calls**: `which_project`, `project_overview`, `glob("radix/src/RX*")` (abandoned — too
broad/unfiltered), `list_tree(radix/src, depth=1)`, `hot_files` scoped to each of RXVERKR, RXVERKS,
RXVERKW, RXVERKW2 (days=365, limit=10, then limit=50 for the two live candidates),
`file_history(tabVKBewegungenEdit.prg, limit=5)`.

**Answer given**: identified the "Verkauf" (sales) module family (RXVERKR/S/W/W2) by inference from
German naming conventions; determined RXVERKW is the most active by both peak-file commit count and
breadth of files touched; named the hottest file (`tabVKBewegungenEdit.prg`), its last change
(2026-09-18, Johannes Feichter, Todo 551083 / WorkItem #28321), and gave three prior commits with
tickets going back to 2026-07-01.

**Metrics**: 80.5s wall clock · 15 tool uses · 94.4K tokens. Reached in full.

**Gaps/judgment calls flagged**: "sales = Verkauf" is an inference from German business vocabulary,
not a verified label match — confirms the doc's premise that Radix's German domain vocabulary is a
real obstacle. "Most active module" (RXVERKW vs RXVERKS) was a judgment call on a moderately close
comparison, since there's no single-call "total commits per directory" aggregate — the agent had to
approximate via top-10/top-50 `hot_files` listings per candidate directory. `glob` on an
unconstrained `RX*` pattern returned 2,000 files with no useful narrowing and was abandoned in favor
of `list_tree`.

---

## 4 — Where is the product actually moving?

**Question asked**: verbatim from the doc.

**Tool calls**: `which_project`, `repo_info`, `project_overview`, `hot_files(days=182, limit=100)`
project-wide, `list_tree(radix/src, depth=1)`, `list_tree(acslib/src, depth=1)`, then
`hot_files(days=182, directory=X, limit=5)` repeated across 10 sampled module folders (RXKOREW,
RXPROW, RXCRMW, RXANLW, RXARTIMPCNET_642, RX1059BW, RX64BASR, Rx64.Tests, AcsDragDropLib, AcsDef).

**Answer given**: used a tool-computed 182-day window (2026-03-20 to 2026-09-18, explicitly stated,
ending at the index's newest commit) as its reading of "six months." Separated real engineering
effort (RXOBJGLB + new Sendcloud shipping integration, sales/PagoPA e-invoicing work, tour-planning
rewrite in RX1655BASW, service/job automation, a new AI-integration layer in acslib) from a
project-wide mechanical version-bump pattern (near-identical small `.xsproj`/AssemblyInfo diffs
appearing on almost every module on a schedule) that it explicitly identified and discounted rather
than mistaking for real churn. Named 5 modules confirmed dormant and ~35 more that fell below even
the noise floor in the top-100 project-wide ranking, with an explicit caveat that not all 35 were
individually re-verified at zero.

**Metrics**: 123.7s wall clock · 18 tool uses · 69.6K tokens. Reached in full, with the "not
individually verified" caveat honestly stated rather than glossed over.

**Gap surfaced**: no per-directory "total commits/lines changed in window N" aggregate exists —
`hot_files` only ranks individual files, so distinguishing "genuinely active module" from "module
that only got a mechanical version-bump" required 10 separate directory-scoped calls plus manual
pattern-spotting (uniform tiny diffs across many unrelated modules) that the agent had to infer
rather than have flagged for it.

---

## 5 — A customer quoted a message back to us

**Question asked**: verbatim from the doc, with the concrete string **"Suchen des Datensatzes nicht
möglich"** (found via a scouting `grep` before subagents launched) substituted for the doc's
"pick a real string" instruction.

**Tool calls**: `which_project`, `grep(query=string, withHistory=true)`, `file_history` on both
matching files, `blame` on 3 line ranges, `project_overview`, `read_file` on the surrounding code
in both files.

**Answer given**: the string appears **3 times in 2 files** — twice in
`RXDOKR/clsArtikelEtiketten/clsArtikelEtiketten.prg` (lines 544, 1106; both last touched by
Matthias Mur, 2023-12-07, PR 19902/Todo 454059) and once in
`RX1834BASW/dtaRx1834AktivierenPreise/clsRx1834AktivierenPreise.prg` (line 139; last touched by
Volkmar Rigo, 2021-07-09, the SourceSafe→Git migration commit). The agent correctly refused to
collapse this into a single answer, explained both candidate modules, and stated it could not
determine which one the customer's screenshot came from without more context. On "has the wording
been edited since it was first written," it explicitly said the toolset cannot answer this (no diff
capability) and gave the best available proxy (no further touching commit for either file/lines).

**Metrics**: 90.4s wall clock · 14 tool uses · 63.1K tokens. Reached as fully as the tools allow;
no confident wrongness — every limit was named rather than glossed over.

**Gap surfaced**: no diff / "show file content as of commit X" tool exists, so "was this line's
wording ever changed, as opposed to just last-touched" is structurally unanswerable from
`blame`+`file_history` alone — the best available signal is "no commit has touched this line since,"
which proves stability going forward but says nothing about whether the December 2023 "improvement"
commit itself changed the wording.

---

## 6 — How does this feature work today? (Vertragsfakturierung)

**Question asked**: verbatim from the doc.

**Tool calls**: 25, led with `which_project`/`repo_info`, then `grep("Vertragsfakturierung")`,
`glob` on the module folder, a long sequence of `list_declarations` calls (main engine class + ~10
related files), `imports` on 2 files, `find_definition`/`find_references` on the base class and
subclasses, `list_tree` on two directories.

**Answer given**: correctly located the feature across three areas — legacy engine
(`RXSERVW2/Vertraege/clsVertraegeFakturierung/`), UI
(`RXSERVW2/Vertraege/dtaVertraegeFakturierung/`), and a newer parallel step-pipeline engine
(`RXSERVW2/clsContractBilling/`) — and gave a per-file outline (not a code dump) of the UI entry
point, the two invoicing tabs (customer/supplier), the 12,188-line core engine and its three
subclasses, the per-contract model, and the modern pipeline's Manager/Context split. Closed with a
plain-language summary suitable for a non-developer.

**Metrics**: 142.3s wall clock · 28 tool uses · 97.1K tokens. Reached almost completely; two
explicit, disclosed gaps below.

**Gaps surfaced (the most important finding of this run)**:
- `imports`/(by extension) `who_imports` **do not work for X#/VO-dialect dependency questions**.
  On both files it checked, every resolved import was a stock .NET namespace (System.*,
  AcsLib.Extensions) — zero project-local dependencies resolved, because VO-style X# code refers to
  types by bare global name rather than `using` imports. The agent had to reconstruct the real
  dependency graph by hand from inheritance clauses and property/parameter types in
  `list_declarations` output — solid evidence, but not what the tool is nominally for, and this is
  exactly the "X# .prg has no using graph a generic tool understands" limitation the source doc
  predicted in its "why the direct arm struggles" note for this question — except here it also
  defeats the *MCP* arm's dedicated tool.
- `list_declarations` truncates at 500 declarations on the 12,188-line core class with no
  pagination/continuation offered — the agent never saw the back half of that file's method
  surface.

---

## 7 — What shipped under this ticket?

**Question asked**: verbatim from the doc, with the concrete ticket **WorkItem #30269 / BugFix
558185** (found via a scouting `git_log` before subagents launched) substituted for the doc's "pick
a real Todo/WorkItem number" instruction.

**Tool calls**: 60 (by far the most of any question) — `which_project`, several `grep`s for the
ticket number as literal text (none hit, since ticket numbers live only in commit messages, not
source), `git_log(limit=200)` which surfaced the exact commit directly, then an extended,
increasingly broad brute-force sweep: `file_history` on roughly 30 candidate files across
`RXMOD770W` and `RXDOKS/Model770` (the module the commit message named), `hot_files` at multiple
day-windows to shortlist candidates, several more `grep`s for related German strings, and finally
`blame` + `read_file(withHistory=true)` on the one file that hit, to pin the exact changed lines.

**Answer given**: found commit `63b094b9` (2026-09-17, Stefano Gennarelli, "BugFix 558185 -
Fehlermeldung Mod. 770 Issue: #30269") directly from `git_log`. Identified the single file it could
confirm the commit touched — `RXDOKS/Model770/sqlMod770DokumenteBewegungen/
sqlMod770DokumenteBewegungen.prg`, lines 366 and 372, inside `ExtractTextFromRTF` — gave a plausible
plain-language description of the fix (a null/whitespace guard before RTF-to-text conversion, to
stop an error surfacing on blank descriptions) inferred from the code and the surrounding history of
related fixes by the same author, and correctly confirmed nothing has changed in that file since
(current state = ticket state).

**Metrics**: 309.7s wall clock · 60 tool uses · 152.7K tokens — roughly 3-4x the cost of a typical
question in this set, driven entirely by the missing capability below.

**Gap surfaced (the single biggest gap of the whole run)**: **there is no "list files touched by
commit X" tool.** `git_log` can find the commit by scanning its message text, but nothing in the
radix MCP surface can then answer "what did that commit change." The agent had to guess which
module the fix probably lived in from the commit message ("Mod. 770"), then brute-force
`file_history` one file at a time across ~30 candidates until one file's history happened to include
that commit hash. This is exactly the scenario the source doc calls out for this question ("pivoting
from a commit to per-file history... several git invocations") — except here even the *MCP* arm pays
that full cost, because the pivot has no direct tool support. The agent's own self-assessment
correctly flags that it cannot rule out the commit having touched an additional file it didn't think
to check.

---

## 8 — What breaks if we change this?

**Question asked**: verbatim from the doc; the agent chose the routine itself.

**Tool calls**: 18 — `which_project`, `repo_info`, `hot_files(days=365)` (found unhelpful — mostly
build/resource files, not source routines), then `grep(filesOnly=true)` on 4 candidate routine names
(`SafeCreateInstance`, `GetRXShell`, `MsgError`, `SqlSelectBase`) to gauge breadth, `find_definition`
+ `find_references` on the chosen routine, `who_imports`/`imports` on its declaring file,
`co_changed` at two window sizes, `file_history`, `list_declarations`, and two `grep`s to check
project-reference breadth across `.xsproj` files.

**Answer given**: picked `SafeCreateInstance` (acslib's core object-construction primitive,
5,225/49,689 files contain the identifier). Separated the three requested "blast radius" kinds
cleanly: direct callers (1,318 confirmed real call sites across the 60 heaviest files, spanning
nearly every business domain), project-reference dependents (26 acslib projects directly reference
the declaring project; radix depends on it only transitively, which the tool can't trace), and
historical co-change (none — the file's Git history was severed by a September 2026 project-rename
commit, so `co_changed` returns nothing at any window). Gave a clear "large/high-risk, core-runtime
change" size estimate with an explicit list of what it could and couldn't verify.

**Metrics**: 149.4s wall clock · 21 tool uses · 98.1K tokens. Reached with real, well-labeled
gaps rather than silent extrapolation.

**Gaps surfaced**:
- `find_references` caps detailed per-file call-site counting at the 60 heaviest files out of
  thousands of matches — no way to get an exact total call count across all matching files, only an
  extrapolation from the sampled top files.
- `who_imports` returns **nothing** for an X# global function like this, for the same structural
  reason as Q6: no file "imports" it at the source-text level; it's visible via project/binary
  reference. This confirms Q6's finding is not a one-off — it's a systemic gap for this codebase's
  dialect.
- `co_changed` goes completely silent once a file's Git history has been severed by an unrelated
  bulk rename — a real, sharp edge for a codebase mid-migration to new naming conventions.

---

## 9 — How consistent are we?

**Question asked**: verbatim from the doc; the agent chose the convention itself.

**Tool calls**: 3 — `which_project`, `repo_info`, and a single
`list_matches(query="\bMsg([A-Za-z]+)\(", group=1, limit=100)` call that did essentially the entire
job.

**Answer given**: 74 distinct spellings of the `Msg<Suffix>(...)` helper family, 33,418 calls across
6,229 files, from one query. Grouped the 74 raw spellings into ~12 semantic families by casing
(ErrorDB, Info, Warning, ErrorRuntime, InternalError, YesNo, ErrorUser, NoYes, Ask, OkCancel,
DeveloperError), gave exact counts and file counts per family, correctly identified `OkCancel` vs
`CancelOk` as a genuine functional difference (button order) rather than a casing variant, flagged a
short list of one-off names as either genuinely distinct helpers or plain typos (`Ingo`, `infoi`),
and reached a clear, evidence-based conclusion: "one convention with a long tail of casing
stragglers, not genuine functional fragmentation" — exactly the shape of answer the source doc asks
for.

**Metrics**: 63.5s wall clock · 5 tool uses · 54.0K tokens — the cheapest tool-call count of any
question and the clearest demonstration in this run of `list_matches` doing exactly what no
grep/sample-based approach could do in one call.

**Self-reported gap (minor)**: the agent's own family-level file-count totals are a hand-summed
upper bound (a file using two casing variants of the same helper would be double-counted across
rows) — it did not re-verify this with a follow-up query, and did not call `find_definition` to
directly confirm that the casing variants compile to one physical method rather than distinct
overloads (it reasoned this from general X#/VO case-insensitivity instead). Not a tool failure, just
an unclosed loop the agent could have closed with one more call.

---

## 10 — The full dossier

**Question asked**: verbatim from the doc; the agent chose the feature area itself (landed on
Vertragsfakturierung again, independently of Q6 — see cross-question note below).

**Tool calls**: 19 shown in its own log (harness counted 23) — `which_project`, `repo_info`,
`project_overview`, keyword `grep`s to locate the feature, `list_tree` on both the legacy and modern
engine folders, `list_declarations` on the core class, `file_history` (40 commits) and `co_changed`
(two windows) on it, `hot_files` scoped to the feature directory, `who_imports` (empty, as expected
per Q6/Q8's finding), `find_references` on two class names, a targeted `grep` for the menu-dispatch
registration string, and `find_definition` on the base class.

**Answer given**: a genuine briefing, not a transcript — covered what the feature does, where each
layer lives (core engine / UI / server-SQL / reporting / finance-integration consumers / the newer
parallel Step-pipeline), the dynamic `SafeCreateInstance`-based menu dispatch pattern (correctly
identifying that this defeats static call-graph tools), ownership (rotating BugFix assignees, no
single owner, most recent = Johannes Feichter's Sept 2026 performance rewrite), a 10-item ticket
history spanning 2023–2026, co-changed files (with 12 of 42 commits explicitly excluded as bulk
refactors, and this exclusion stated rather than hidden), a full "everything that depends on it"
list, and a cost estimate for a moderate change with reasoning tied to file size and change cadence
rather than asserted from nothing. The "uncertain about" section was real and specific (7 distinct,
concrete caveats), matching exactly what the source doc calls for in this question.

**Metrics**: 147.9s wall clock · 23 tool uses · 106.1K tokens. The agent explicitly reported that it
felt context budget as a constraint and made a deliberate, disclosed choice to use cheap structured
tools (`list_declarations`, `file_history`, `co_changed`) instead of `read_file` on the 8,000-line
core file, and stopped once the picture was coherent rather than exhaustively reading every
subsystem — this is precisely the "briefing not a transcript, honest uncertainty" behavior the
source doc's "a good answer" bar asks for, and it happened without truncating silently.

**Gaps surfaced**: confirms, for a third time in this run (after Q6 and Q8), that `who_imports`/
`imports` are structurally blind to X#'s dynamic-dispatch/global-visibility idiom. Also confirms
Q8's finding that `co_changed` silently drops bulk-refactor commits (here: 12 of 42) from its
pairing — useful behavior, but not surfaced anywhere in the tool's own output; the agent had to
notice the exclusion itself.

---

## Overall evaluation

### Tool coverage vs. the doc's predicted table

Every tool the doc predicted got exercised, largely on the questions it predicted:
`which_project`/`repo_info`/`project_overview` opened almost every run (used far more broadly than
the table implies — every subagent reached for orientation tools first, not just Q1/Q10).
`list_matches` (Q9) was the single cleanest success in the whole run — one call, exact counts, no
sampling. `hot_files`, `git_log`, `authors`, `file_history`, `blame`, `co_changed`,
`find_references`, `who_imports`, `imports`, `list_declarations`, `grep`, `glob`, `find_definition`
were all used roughly where predicted (Q3–Q10). The doc's own prediction that `who_imports`,
`co_changed`, `list_matches`, and `list_declarations` are the tools with no direct-access equivalent,
concentrated in Q6/Q8/Q9, held up — but two of those four (`who_imports`, and `imports` by
extension) turned out to be **not just "no direct-access equivalent" but actively non-functional for
this codebase's dialect** (see below), which is a stronger and more actionable finding than the
doc's framing suggested.

### Tool calls that failed, returned something unhelpful, or forced a workaround

- **`imports` / `who_imports` on X#/VO `.prg` files (Q6, Q8, Q10, all three independently)**:
  return only unresolved stock-.NET namespaces or nothing at all, because this codebase's dialect
  expresses dependencies via bare global-name visibility and dynamic `SafeCreateInstance` dispatch,
  not source-level imports. Every subagent that hit this correctly diagnosed it and fell back to
  inferring dependencies from inheritance/parameter types (`list_declarations`) or from textual
  `grep`/`find_references` on the class name — a real workaround, done well, but repeated
  independently three times at real token/call cost.
- **No "list files changed by commit X" tool (Q7)**: this was the single most expensive gap in the
  run — 60 tool calls and over 5 minutes, roughly 3-4x every other question, spent brute-forcing
  `file_history` across ~30 candidate files to find the one the target commit touched. `git_log` can
  find a commit by message text, but nothing bridges "here's a commit hash" to "here are its
  changed paths."
- **No "oldest commit" / total-commit-count on `git_log` (Q1)**: 15 of 28 tool calls went to a
  manual binary search over `page`/`limit=1` just to bound each repo's history start.
- **No per-directory aggregate churn stat (Q3, Q4)**: `hot_files` only ranks individual files, so
  "which module is busiest" or "which modules are real vs. mechanically version-bumped" required
  5-10 separate directory-scoped `hot_files` calls per question, with the agent doing the
  aggregation and pattern-spotting by hand.
- **`find_references` silently caps detailed inspection at the top 60 files (Q8)**: fine as a
  performance safeguard, but there's no way to ask for "the total count across all matches" — the
  agent had to extrapolate rather than report an exact number.
- **`list_declarations` truncates at 500 entries with no pagination (Q6, Q10, both independently)**:
  on the two largest classes in the codebase (8,000+ and 12,000+ lines), the agent never saw the
  back half of the class's declared members.
- **`co_changed` goes silent with no explanation after a rename severs history, and silently drops
  bulk-refactor commits from pairing (Q8, Q10)**: both are reasonable design choices, but the tool
  doesn't say so in its output — the agent had to infer "this file was renamed, that's why there's
  no data" (Q8) or notice on its own that 12 of 42 commits were excluded (Q10).
- **`glob` on an unconstrained prefix pattern (Q3)**: `glob("radix/src/RX*")` returned 2,000 files
  with no useful narrowing and was abandoned in favor of `list_tree` — not a hard failure, but an
  awkward first move that a depth/extension hint in the tool's own description might have
  prevented.

### Missing functionality worth adding — concrete, cited

1. **A "commit → files changed" tool** (e.g. `commit_files(sha)` or a `files` field added to
   `git_log`'s output when a single commit is requested). *Evidence: Q7* — this single gap cost 60
   tool calls and 5+ minutes, the most expensive question in the run by a wide margin, entirely
   because there is no way to go from a commit hash to its changed paths except brute-forcing
   `file_history` across guessed candidate files.

2. **A "first/oldest commit" or total-commit-count field on `repo_info` or `git_log`** (e.g.
   `git_log(order="oldest")` or a `totalCommits` field in `repo_info`'s response). *Evidence: Q1* —
   15 of 28 tool calls were a manual binary search over `page`/`limit=1` to answer "how far back
   does history go," a question the doc explicitly calls out as one `repo_info` should be well
   suited for.

3. **A directory-scoped aggregate for `hot_files`** — e.g. a `groupByDirectory` or `depth` param
   that returns one row per top-level module folder with total commits/lines-changed in the window,
   rather than only a flat file ranking. *Evidence: Q3, Q4* — both questions needed "which module is
   busiest," and both agents had to fan out 5-10 directory-scoped `hot_files` calls and aggregate by
   hand; Q4's agent additionally had to hand-detect a project-wide mechanical version-bump pattern
   that a "real code churn vs. metadata-only churn" flag on the tool's output would surface for
   free.

4. **Either make `imports`/`who_imports` X#/VO-aware (resolve types by bare global-name visibility,
   not just `using`-style imports), or have the tool say explicitly "this dialect has no source-level
   import graph; try `find_references`/`SafeCreateInstance` search instead."** *Evidence: Q6, Q8,
   Q10* — three independent subagents hit the exact same silent near-empty response and had to
   rediscover the same workaround from scratch, each paying the diagnosis cost itself. A short,
   explicit note in the tool's response (rather than just quietly returning framework-only imports)
   would save real tool calls and prevent an agent from momentarily believing a file has almost no
   dependencies.

5. **Pagination/continuation for `list_declarations` past its 500-entry cap.** *Evidence: Q6, Q10*
   — both hit this independently on the two largest classes in the codebase; neither agent ever saw
   the back half of either class's declared members, which is a real information gap for exactly the
   kind of huge, old procedural files this codebase is full of.

6. **Surface `co_changed`'s exclusion rules in its own output** (e.g. "12 commits excluded as bulk
   changes (>200 files touched)" or "no history before rename — see file_history"), instead of
   leaving the agent to infer or notice the exclusion itself. *Evidence: Q8 (rename → total silence,
   undiagnosed by the tool), Q10 (12/42 commits silently dropped, only caught because the agent
   happened to compare counts)*.

7. **A way to get an exact total match/reference count beyond the top-N heaviest files**, at least
   as a count-only field, for `find_references`/`grep` on very broad symbols. *Evidence: Q8* — the
   agent could only report "1,318 confirmed calls in the 60 heaviest of 5,059 matching files," with
   the true total left as an unverified extrapolation for a question the doc frames as needing a
   "rough size for the change."

None of the 10 questions produced a confidently-wrong answer — every subagent that hit a real limit
named it explicitly rather than guessing past it, which is itself a notable result given the doc's
framing that "unobtainable, incomplete without saying so, or wrong" is the actual failure bar. The
one recurring soft failure mode across this run wasn't wrong answers, it was **expensive rediscovery
of the same workarounds** (the X# imports gap, three times; the commit→files gap, once but very
expensively) — the kind of cost that a small number of targeted tool improvements above would remove
for every future run, MCP or otherwise.
