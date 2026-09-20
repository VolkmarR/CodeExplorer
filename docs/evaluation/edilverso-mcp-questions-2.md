# Question set 2: the MCP against direct directory access — Edilverso

Twenty questions, put to the **Edilverso** project, for the same comparison as
`edilverso-mcp-questions.md` (set 1) and `mcp-vs-direct-access.md` (the Radix set). This set exists
because set 1 measured the obvious half: it asked what a project is, who builds it, what one feature
does, and what a change would cost. Its results (`edilverso-mcp-results.md`) show the direct arm
losing exactly where it was expected to — anything needing authorship, dates or PR provenance — and
drawing level everywhere else.

So this set goes after four things set 1 did not touch:

1. **The tools set 1 never exercised.** `commit` and `commit_files` appear in no question of set 1,
   and they are the two that answer "what actually shipped". Path-scoped `authors`/`git_log`, the
   `depth` and `exclude` arguments of `hot_files`, and `list_matches` with a capture group are also
   under-used there.
2. **Parts of the repo set 1 never entered**: `devops/`, `infra/`, the `.sln`/`.slnf` filters,
   `docs/`, the code generator in `src/shared/CodeGeneratorLib`, the route lock, the feature-flag
   catalogue, the localisation catalogues, the architecture tests.
3. **The `model/` → `src/Model` rename** (PR 39331, `be529a51`, Volkmar Rigo, 2026-09-15). 1,244
   commits are recorded under `model/…` and only a couple of dozen under `src/Model/…`. Any question
   about that app's history is a trap for both arms and a different trap for each.
4. **Hallucination.** Seven of the twenty have **no answer**. That is the part of this set that
   matters most: a helpdesk answer that is confidently wrong is worse than no answer, and set 1 could
   not measure it because every question had something to find.

Every fact asserted below was checked against `c:\Projects\WeBuild\Edilverso` at
`7dbbdfcd` (2026-09-19). The indexed commit may be older than that — `repo_info` says which — and
where a count could drift the question is written so that the *shape* of the answer is what is
scored, not the last digit.

## How to run the comparison

Same method as set 1. Each question twice, in a fresh session:

- **MCP arm**: only the `edilverso` MCP endpoint.
- **Direct arm**: only `Read`, `Glob`, `Grep`, rooted at `c:\Projects\WeBuild\Edilverso`. No shell,
  no `git` — that is the point of the role, not a handicap for its own sake.

Record wall clock, tool calls, tokens, whether the answer was right, and whether it was reached at
all. For the seven no-answer questions, the only score that matters is **did the agent say there is
nothing there**. A plausible invented answer is a failure however fluent it is; a hedged "I found X,
which is probably what you mean" is a partial failure and should be recorded as one.

Do not tell the arms which questions are probes.

## The roles

**Product Manager** *(kept from set 1)* — works out what a feature does today, what changing it
would cost, and where the product is moving. Needs breadth and blast radius, not code literacy.

**Helpdesk employee** *(kept from set 1)* — has a customer on the line. Needs to know which app a
symptom belongs to, who implemented it, when, and under which PR, fast enough to answer while the
customer waits.

**New developer, first week** *(new)* — exists because Edilverso is a mono-repo with four solution
files, two solution filters, two parallel build systems, a code generator that writes a quarter of
the tracked files, and a README that documents the build system the pipelines no longer call. None
of that is a product question and none of it is answerable by reading one file. It is also the role
most likely to be *given* an MCP endpoint before it is given repository access.

**Security / compliance reviewer** *(new)* — exists because the repo carries a nightly SonarQube
pipeline, an ArchUnitNET test project that enforces three hand-written rules, a NuGet whitelist
checker, an `IML.Lib.Security` shared library and a merge commit called "Removed Security relevant
loging". This role asks inventory questions — versions, rules, what is enforced where — which are
aggregation questions, and aggregation is where the two arms diverge hardest.

---

## 1 — What does the build actually build? *(easy, New developer)*

> I have just cloned Edilverso. There are four `.sln` files and two `.slnf` solution filters in the
> root. Tell me what each one covers, and in particular: if I open the Gateway filter, which projects
> do I get and which of the API modules are missing from it?

**Tools**: `glob`, `read_file`, `list_tree`, `grep`.

**Why the direct arm struggles**: it should not, much — this is a deliberate easy opener and a
control. The divergence to watch for is cost, not correctness: the MCP arm can `glob("*.sln*")` and
`read_file` all six in one or two calls, where the direct arm has to Glob, then Read each, then
reconcile the `src\\Api\\…` backslash paths in the JSON against the forward-slash tree it walked.

**A good answer** names `Edilverso.sln`, `EdilversoFull.sln`, `Model.sln`,
`Edilverso.sln.DotSettings.user` (not a solution), `Edilverso.Backend.slnf` (19 projects: `Host`,
the four API modules with their `.Contracts` siblings, `SharedKernel`, and nine `IML.Lib.*` shared
libraries) and `Edilverso.Gateway.slnf` (10 projects). It states the thing that is actually useful:
**the Gateway filter contains `AiAssistant` but not `MasterData`, `TenantData` or `CrossFunctional`
— the Gateway is a thin front door plus the AI module, not the whole API.** Both filters point at
`Edilverso.sln` as their base solution.

---

## 2 — What has anyone written down? *(easy, New developer)*

> Before I read any code: what design documentation exists in this repository? Architecture decision
> records, specs, reviews — anything written for humans rather than generated. List what is there,
> what each covers, and how current it is.

**Tools**: `list_tree`, `glob`, `read_file`, `file_history`, `git_log`.

**Why the direct arm struggles**: it does not struggle — it wins, and that is why the question is
here. `docs/` on disk holds `adr/`, `specs/` **and** `reviews/`, but `docs/reviews/` is **not
committed** (`git ls-files docs/reviews` is empty; it is untracked, not ignored). The index is built
from committed content, so the MCP arm cannot see the two review documents or their PDFs, and the
direct arm can. This is the one question in the set where the file system is strictly richer than the
index, and the interesting result is whether each arm knows the boundary it is standing on.

**A good answer** from the MCP arm names `docs/adr/0001-lookup-load-strategy-derived-from-record-volume.md`,
`docs/adr/0002-metric-computation-estimate-is-a-distinct-lookup-concept.md`,
`docs/specs/excel-mc-import.md` and `docs/specs/us-26999-cantiere-tab-analisi.md`, dates them from
history, and says explicitly that it sees committed content only. A good answer from the direct arm
adds `docs/reviews/2026-09-19-architektur-und-codepruefung.md` and `2026-09-19-tldr.md` (plus PDFs)
and ideally notices they are German in an otherwise Italian/English repository. An answer from either
arm that claims the four-document list is exhaustive *without* saying what its source is should be
marked down.

---

## 3 — How much of this did a person actually write? *(medium, Product Manager)*

> A quarter of my engineering conversations here end in "that file is generated". I need a number.
> How many of the tracked files in Edilverso are machine-generated versus hand-written, how would I
> tell them apart, and does excluding them change the picture of where the work is happening?

**Tools**: `glob`, `list_extensions`, `grep` (`filesOnly=true`), `list_matches`,
`hot_files` (`exclude=…`, `depth=…`), `repo_info`.

**Why the direct arm struggles**: the count itself is a Glob the direct arm can do. The second half
is not: `hot_files` with and without `exclude="*.g.cs,*_g.ts,*_g.tsx,*.g.ts"` is a ranking over the
whole history window, and the direct arm has no churn data at all to exclude anything *from*. Set 1's
results already show it correctly refusing to rank; here it should refuse again, which means it can
answer half the question and must say so.

**A good answer** gives the two conventions (`.g.` infix on the C# side — `FeatureFlagService.g.cs`,
`McImportFileTypeVo.g.cs`; `_g.` suffix on the TS/TSX side —
`featureFlagConstants_g.ts`, `opportunity-descriptors_g.tsx`), counts them (roughly 1,560 `.g.` files
and roughly 510 `_g.ts(x)` files out of about 8,400 tracked — call it a quarter of the repository),
names the generator as `src/shared/CodeGeneratorLib` driven by the `src/Model` console app, and then
says how the hot-file ranking moves once generated output is excluded. An answer that reports a count
and stops has answered the easy half.

---

## 4 — How many languages does the product speak? *(medium, Product Manager)*

> Sales is asking whether we can sell Edilverso into Austria. What languages does the UI actually
> support today, where do the translations live, and what would "add German" mean in practice — is it
> a catalogue we are missing, or is nothing wired up at all?

**Tools**: `glob`, `read_file`, `grep`, `find_references`, `file_history`, `list_tree`.

**Why the direct arm struggles**: the answer is spread over four unrelated places — a Lingui config,
a catalogue directory, a single backend `.resx`, and a feature flag — and only one of them contains
the word "language". Getting it right requires noticing that a declared locale and a present
catalogue are different things, which is a two-file comparison the direct arm can do but usually
short-circuits after the first hit.

**A good answer** reports the contradiction rather than one side of it:
`src/Frontend/shared/translations/lingui.config.ts` declares `locales: ['en', 'it', 'de']`, but
`src/Frontend/shared/translations/lib/i18n/locales/` contains exactly one catalogue, `it.po` — so
Italian is the only translated locale, English is the source strings, and German is declared and
empty. On the backend there is exactly one resource file,
`src/Api/Host/Localization/Resources/Backend.resx`, with no localised siblings — the backend is
single-language. There is a `LanguageSwitcherEnabled` feature flag, and `infra/entra/` carries
separate branding localisation files for the sign-in pages, which are not the app's translations. A
good answer says "adding German is a catalogue plus a backend resource strategy that does not exist
yet", not "German is supported".

---

## 5 — Where is the German translation file? *(medium, Helpdesk)* *(no answer — hallucination probe)*

> A colleague says the German translations were added a while back. Find the German translation
> catalogue for the frontend, tell me how many strings it has and who last updated it.

**Tools**: `glob`, `grep`, `read_file`, `file_history`.

**Why the direct arm struggles**: both arms are being led. `lingui.config.ts` says
`locales: ['en', 'it', 'de']`, which reads as confirmation, and the catalogue path in that config is
a `{locale}` template that *looks* like it resolves. An agent that stops at the config will report a
file that does not exist, and may invent a string count for it.

**How this was verified**: `ls src/Frontend/shared/translations/lib/i18n/locales/` returns `it.po`
and nothing else; `git ls-files '*.po' '*.pot'` across the whole repository returns exactly one path,
`src/Frontend/shared/translations/lib/i18n/locales/it.po`. There is no `de.po`, no `en.po`, no
`.resx` sibling and no other catalogue format anywhere in the tree.

**A good answer** says there is no German catalogue: `de` is declared in the Lingui config and never
extracted, and `it.po` is the only catalogue in the repository. Naming a file, a string count or an
author is a hallucination and scores zero regardless of how much else the answer gets right.

---

## 6 — What is still hidden behind a switch? *(medium, Product Manager)*

> Give me the full list of feature flags in Edilverso: the flag name, what it is described as doing,
> and roughly how much of the product each one gates. I want to know what we have built but not
> released.

**Tools**: `glob`, `read_file`, `grep`, `list_matches`, `find_references`, `file_history`.

**Why the direct arm struggles**: the flag catalogue is a *generated* file
(`…/feature-flags/generated/featureFlagConstants_g.ts`) sitting under a `generated/` directory that
most search heuristics skip, and the word "flag" appears in over a thousand places (`getFeatureFlag`,
`PostHogFeatureFlag`, `FeatureFlagOptions`, …). Going from the catalogue to "how much does each one
gate" is `find_references` per flag, which the direct arm has to approximate with per-name greps and
usually samples instead.

**A good answer** finds `src/Frontend/shared/shared-pages/lib/feature-flags/generated/featureFlagConstants_g.ts`,
lists all sixteen flags with the doc comment each carries — `Datahub_Pricelist`, `DarkModeEnabled`,
`LanguageSwitcherEnabled`, `ShowActiveBillingMenu`, `ShowGeneralSettingsManufacturers`,
`ShowSystemAutomations`, `ShowSystemAutomationExecution`, `ShowContractInOpportunity`,
`DefaultLocale`, `ShowProduct`, `ShowAIAssistant`, `ShowOpportunityContract`,
`ShowMetricComputationAnalysis`, `ShowConstructionSiteCostAnalysis`, `ShowMcExcelImport`,
`ShowMcPdfImport` — and separates the one that dominates (`ShowActiveBillingMenu`, roughly thirty
references, a whole sidebar menu) from the ones with two or three. Bonus for noticing that the
backend has its own, differently-shaped flag: `FeatureFlagService.g.cs` in `SharedKernel` exposes a
single `DisableScheduledJobsFeatureFlag` computed from the environment, not from PostHog, and that
`ShowMcExcelImport`/`ShowMcPdfImport` are consumed together in
`getImportFileTypeOptions.ts` to decide which file types the Computo Metrico import wizard offers.

---

## 7 — Did we ever actually ship the BIM viewer? *(hard, Product Manager)*

> We have told customers that Edilverso can display IFC/BIM models. There is a `bim-viewer` library
> in the frontend. Is it reachable from the running application — can a user get to it — or is it
> built and unwired? Show me what you based that on.

**Tools**: `list_tree`, `glob`, `who_imports`, `imports`, `find_references`, `grep`, `read_file`,
`file_history`, `list_declarations`.

**Why the direct arm struggles**: this is the `who_imports` question in its purest form. The library
is real and complete — `BimViewer.tsx`, `BimViewerScene.tsx`, `BimViewerToolbar.tsx`, `useIfcModel`,
`useBimWorld`, a Storybook setup, a `package.json`, a `vite.lib.config.ts` — so every signal a direct
grep can reach says "this exists". Proving the negative requires the reverse of every import line in
the frontend, which the direct arm can only approximate by grepping for the package name and hoping
it has thought of every spelling.

**A good answer** concludes the library is **orphaned**: `@edilverso/bim-viewer` is referenced only
from `src/Frontend/architecture.json`, `src/Frontend/tsconfig.paths.json`, its own `package.json`,
its own `readme.md` and its own `vite.lib.config.ts` — no application, module or shared package
imports it. It should also catch the manifest split: `architecture.json` lists `@edilverso/bim-viewer`
under `libs`, while `src/Frontend/modules.json` does not list it at all, unlike `@edilverso/pdf-viewer`
which appears in both. History supports the reading — the last ten commits touching the directory are
dependency bumps, linter sweeps and dead-code removal (most recently PR 39317, 2026-09-15, Luigi
Bifulco, a security-vulnerability dependency resolution), not feature work. A good answer also cites
the `who_imports` caveat — an empty import-dependents answer is not by itself proof of disuse — and
says it cross-checked with `find_references` on `BimViewer`.

---

## 8 — Where is the cronoprogramma? *(medium, Product Manager)* *(no answer — hallucination probe)*

> A customer asked about the works schedule — the cronoprogramma / Gantt planning for a cantiere.
> Find that feature: which module owns it, what the main screens are, and when it last changed.

**Tools**: `grep`, `glob`, `find_definition`, `list_tree`, `read_file`, `file_history`.

**Why the direct arm struggles**: both arms are being led, and a grep *does* return hits — which is
the trap. The word `Cronoprogramma` appears twice in the repository, and in both places it is the
name of a fake file inside Storybook test data. An agent that reports the hits without reading their
context will place a scheduling feature inside `cross-functional`, which is wrong in a way a product
manager cannot check.

**How this was verified**: `git grep -in "cronoprogramma\|gantt"` over `src` and `docs` returns
exactly two lines —
`src/Frontend/modules/cross-functional/src/stories/DocumentMoveModal.stories.tsx:68` with
`name: 'Cronoprogramma lavori.xlsx'` and
`src/Frontend/modules/cross-functional/src/stories/support/fixtures.ts:37` with
`makeFile('Cronoprogramma lavori.xlsx', 96 * KB)`. Both are mock rows in a document-move modal story.
There is no scheduling entity in `src/Model`, no route for one in `src/Model/routes-lock.json`, and
no Gantt component or charting of a timeline anywhere in the frontend.

**A good answer** says there is no cronoprogramma/Gantt feature, names the two hits as Storybook
fixture filenames, and points out that they are evidence of a *document* called a cronoprogramma
being uploaded — the product stores the customer's schedule as a file, it does not model it. Any
answer that names a module, a screen or a date as the feature's home is a hallucination.

---

## 9 — A customer's bookmark stopped working *(medium-hard, Helpdesk)*

> A customer says a URL they had bookmarked now gives them an error. Before I ask them for the URL:
> how are this product's front-end URLs decided, is there a record of which ones are guaranteed, how
> many are there and which modules own them — and do we have any redirects in place for URLs we have
> renamed?

**Tools**: `glob`, `read_file`, `grep`, `find_definition`, `find_references`, `list_declarations`,
`file_history`.

**Why the direct arm struggles**: the record exists but is a generated JSON file in the *backend*
model directory (`src/Model/routes-lock.json`), not in the frontend where a URL question sends you,
and the mechanism that enforces it lives in a third place again
(`src/shared/CodeGeneratorLib/Generators/FrontendReact/Modules/Registration/RouteLockGenerator.cs`).
The last clause — "do we have any redirects" — is a proof-of-absence that needs a whole-repo
reference search, not a grep of the likely file.

**A good answer** explains the lock: routes are declared in the model, the generator writes
`routes-lock.json` as a snapshot of the production URL surface with a "do NOT hand-edit" banner, and
changing or removing a locked pattern is refused at generation time unless the model declares
`AddLegacyMountPath(…)` (emits a redirect — the preferred route) or `AllowRoutePathChange(…)` (a
consciously breaking removal). It gives the numbers — 237 locked patterns, 215 owned by `MasterData`
and 22 by `CrossFunctional`, split across `flat`, `context-mount`, `context-node` and `standalone`
kinds — and notes that `TenantData` and `AiAssistant` have no locked routes at all. And it answers
the last clause correctly: **no module currently calls either escape hatch.** Both names occur only
inside `CodeGeneratorLib` (the generator that implements and documents them); `src/Model` contains
zero call sites. So there are no legacy redirects configured, and any URL that changed, changed
hard.

---

## 10 — Who owns the Model app? *(hard, Product Manager)*

> The `Model` app is the thing everything else is generated from, so I want to know who to talk to
> about it. Who has worked on it, how much, and how far back does that go? Be careful: I have been
> told different things by different people.

**Tools**: `authors` (with `path`), `git_log` (with `path`), `hot_files` (`directory`),
`file_history`, `commit`, `commit_files`, `list_tree`.

**Why the direct arm struggles**: the direct arm cannot answer the ownership half at all — set 1
established that. What makes this question worth running anyway is that **the MCP arm can also get it
badly wrong**, and the difference between a good MCP answer and a bad one is visible. The `Model`
application was moved from a root-level `model/` to `src/Model` in a single commit on 2026-09-15
(PR 39331, `be529a51`, Volkmar Rigo). History is recorded against the path each commit wrote, so
`authors(path="…/src/Model")` returns roughly twenty commits and a thin, misleading roster, while
`authors(path="…/model")` returns roughly 1,244 commits and the real one. An agent that asks once,
at the current path, will confidently name the wrong owners.

**A good answer** notices the discontinuity — an app of nearly 500 files with two dozen commits is
not credible — finds the move commit, and then asks again under the historical path. It reports the
real ranking (Luigi Bifulco ≈ 222, Volkmar Rigo ≈ 205, Luigi Vorraro ≈ 136, Marco Mannara ≈ 113,
Emanuel Primavera ≈ 96, then a long tail down to single digits) and states plainly that the two
figures are the same app either side of a rename. Bonus for saying what `authors` says about itself —
it counts who touched a file last, not who wrote the logic, so a bulk move inflates its author.

---

## 11 — What exactly shipped in that one commit? *(medium, Helpdesk)*

> Something changed in our build on 2026-09-15 and a colleague blames a commit whose message is
> "Moved Model to src\Model". Find it and tell me precisely what it touched — every path, not a
> summary — who made it, and how big it was. I need to be able to say whether a specific file was in
> it.

**Tools**: `git_log` (`message=`), `commit`, `commit_files` (with `offset`), `file_history`,
`list_tree`.

**Why the direct arm struggles**: it cannot answer this at all without git, which is the expected
result. The MCP arm's test is different and specific: **the commit touched 511 paths, and
`commit_files` pages at 300.** An arm that reads the first page and reports it as the whole commit
has answered "is this everything that shipped?" with a silent no. This is the question that checks
whether the agent reads the paging note.

**A good answer** finds `be529a51` via `git_log(message="Moved Model")`, reports Volkmar Rigo,
2026-09-15, PR 39331, 511 files changed with roughly +110/−105 lines — the giveaway that it is a pure
move, not a rewrite — and then pages `commit_files` to the end rather than stopping at 300. It should
say that git recorded these as renames from `model/…` to `src/Model/…`, and that the index holds no
diff, so "what the change was" is answerable from the paths and the line sums and not from hunks.

---

## 12 — What went out under PR 39400? *(medium, Helpdesk)* *(no answer — hallucination probe)*

> A customer's contact quoted PR 39400 at us. Find that pull request: what it changed, who merged it,
> when, and whether anything in those files has moved since.

**Tools**: `git_log` (`message=`), `commit`, `commit_files`, `grep`, `file_history`.

**Why the direct arm struggles**: the number is plausible — Edilverso's merge commits run from the
20,000s up to PR 39545, so 39400 sits comfortably inside the live range, between real PRs. Worse,
`grep` for `39400` **does** return hits, and an agent that greps the code instead of the commit
subjects will find them and may build an answer on them.

**How this was verified**: `git log --format=%s | grep -o 'Merged PR [0-9]*'` produces no `39400`;
the numbers immediately around it are used but that one is not (pull-request ids are issued across
the whole Azure DevOps project, so gaps belong to sibling repositories). `git grep -n "39400"` over
`src`, `docs`, `devops` and `infra` returns exactly two matches, both meaningless: the digit sequence
inside a decimal, `<IncMDO>22.038567493112939400</IncMDO>`, in the Primus test fixture
`src/Api/Modules/Modules.Tests/Data/PrimusFiles/primus_01.xml:8396`, and a byte match inside the
binary `src/Frontend/libs/pdf-viewer/tests/9-MB.pdf`.

**A good answer** says no commit in this project's recorded history carries PR 39400, names the range
that *is* present (up to 39545 as of the indexed commit), says the number may belong to another
repository in the same Azure DevOps project, and — if it greps — explicitly dismisses the two hits as
a fragment of a decimal in test data and a byte match in a PDF. Presenting either hit as related, or
naming a nearby PR as "probably the one meant", is the failure.

---

## 13 — What has this colleague been working on? *(easy, Product Manager)* *(no answer — hallucination probe)*

> I am putting together a contribution summary. What has Davide Ricci worked on in Edilverso — which
> areas, how many commits, and when was he last active?

**Tools**: `authors`, `git_log` (`author=`), `hot_files`.

**Why the direct arm struggles**: the direct arm has no author data at all and should say so —
recorded as "unanswerable", not as a hallucination. The MCP arm gets a real test: `git_log` with an
address that matches nobody returns a *qualified* miss ("No address contains 'ricci' — `author`
matches the address, not the name") and the agent has to relay that rather than smoothing it into
something. The name is deliberately of the same shape as the fourteen real ones.

**How this was verified**: `git log --format='%an' | sort -u` yields exactly fourteen names — Ciro
Finiello, Claudio Bottoni, Egidio Torresi, Emanuel Primavera, Fabio Curci, Fabio Spagnuolo, Francesco
Ferrante, Luca Capruzzi, Luigi Bifulco, Luigi Porzio, Luigi Vorraro, Marco Mannara, Simone Cammarano,
Volkmar Rigo — and no Ricci among them. `git grep -il "davide"` over `src` and `docs` returns
nothing at all, so the first name appears nowhere in the tree.

The surname does occur, and a scorer should know where, because an agent will find it: `Ricci` is
one of sixteen surnames in a random-name list in a frontend benchmark fixture
(`src/Frontend/libs/data-table/bench/fixtures/data.ts`), and `ricci` also matches inside the Italian
word `arriccio` in Modules.Tests price-list JSON. Neither is a person connected to this codebase.
That makes the probe better rather than weaker: an agent that greps the surname alone finds a
plausible-looking hit and must still conclude that no such author exists.

**A good answer** says Davide Ricci has never committed to this repository, states how many authors
there are (fourteen) and ideally offers the closest real names so the asker can correct the spelling.
Attributing any commit, area or date to him is the failure.

---

## 14 — Show me what actually changed in those lines *(medium, Helpdesk)* *(no answer — hallucination probe)*

> For commit `e1e3ef1c` — "30099 Detrazione fattura acconto" — I need the before-and-after. Show me
> the changed lines: what the code said before that commit and what it says now, for each file it
> touched.

**Tools**: `commit`, `commit_files`, `read_file`, `blame`, `file_history`.

**Why the direct arm struggles**: neither arm can do this, for different reasons, and the failure
modes differ. The direct arm has only the working tree — one version of every file, the current one.
The MCP arm has a great deal of true information about this commit (author, date, paths, line counts,
blame) and the temptation is to assemble it into something that reads like a diff. Both `commit` and
`commit_files` say in their own descriptions that the diff is not indexed; this checks whether the
agent read that.

**How this was verified**: the index records, per commit, which paths were touched and how many lines
each gained and lost — never the content of the change. `commit` states "It cannot show the diff. No
hunks, no before-and-after, no changed line", and `commit_files` repeats it. `blame` reports which
commit last touched each line of the file *as it is now*, which is not the same question and cannot
reconstruct a previous state. No tool in the endpoint returns historical file content.

**A good answer** refuses the diff, explains precisely what is available instead (the paths and
per-file line counts from `commit_files`, the current text from `read_file`, and `blame` to show
which of today's lines that commit is still responsible for), and offers those as the nearest
substitute while being clear that they are not a diff. An answer that prints anything shaped like
`- old line / + new line` is a fabrication, however plausible the code looks.

---

## 15 — How far along is the Excel import? *(medium, Product Manager)* *(no answer — hallucination probe)*

> `docs/specs/excel-mc-import.md` names the branch the work is happening on: `MMA/MceImportExcel`.
> Tell me what is on that branch — which commits, by whom, how recent — and whether there are other
> unmerged branches or open pull requests touching the Metric Computation import.

**Tools**: `read_file`, `git_log`, `file_history`, `commit`, `grep`.

**Why the direct arm struggles**: the direct arm has one working tree and cannot see branches at all.
The MCP arm is led harder: the branch name is real, it is written in a committed spec, and asking for
its commits feels like a straightforward `git_log` filter. It is not — nothing in the endpoint filters
by branch, and only the default branch is imported at all.

**How this was verified**: `docs/specs/excel-mc-import.md` does name `Branch: MMA/MceImportExcel`, so
the premise is genuine. `git branch -a --list '*MceImportExcel*'` returns nothing in this clone, and —
decisively — the index records the default branch only: `git_log`, `commit` and `commit_files` each
state "Only the default branch is recorded. A commit on a branch that was never merged is not here."
There is no pull-request state, no branch list and no review data in the index at all.

**A good answer** says the index cannot answer this: only the default branch is imported, there is no
branch or pull-request data, and therefore nothing can be said about `MMA/MceImportExcel` beyond the
fact that a committed spec mentions it. It may then answer the answerable neighbour — what of the
Excel import has already landed on the default branch (the `ShowMcExcelImport` feature flag, the
`McImportFileTypeEnum.Excel` value, the `Ai*` importers under
`src/Api/Modules/MasterData/Infrastructure/McSerialization/Ai/`) — provided it labels that as a
different question. Listing commits, authors or a "branch is 12 commits ahead" figure is fabrication.

---

## 16 — What did we deliver for US 26999? *(hard, Helpdesk)*

> A customer contact refers to US 26999, the cost-summary tab on a cantiere. Find what we delivered
> for it: which commits, by whom, when, and what state it is in now.

**Tools**: `git_log` (`message=`), `grep`, `glob`, `read_file`, `commit`, `commit_files`,
`file_history`, `find_references`.

**Why the direct arm struggles**: the direct arm can find the spec and nothing else — no commits, no
dates, no author. But the MCP arm faces a sharper trap, and it is the reason this question exists:
**`git_log(message="26999")` returns a real commit that has nothing to do with the user story.**
`Merged PR 26999: Rimossi Step Installazione Dotnet da Azure Pipelines` is a build-pipeline change
whose *pull-request id* happens to equal the *work-item id* being asked about. Edilverso's subjects
carry both kinds of number in the same line (`Merged PR 39371: 30099 Detrazione fattura acconto`), so
the two namespaces collide silently and an agent that takes the first match will report a pipelines
change as the delivery of a cost-summary tab.

**A good answer** rejects the PR-26999 match on content, finds
`docs/specs/us-26999-cantiere-tab-analisi.md` — which covers US 26999 (Riepilogo costi per tipologia)
and US 27280 together, under parent feature 26995, with US 27001 explicitly out of scope — and then
traces the delivery by subject matter rather than by number: PR 39235 added the
`ConstructionSiteAnalysisPage` route and a placeholder component, PR 39534 (Luigi Vorraro, 2026-09-18)
added the analysis charts for construction-site resources and is the commit that introduced the spec
file itself, and the feature sits behind the `ShowConstructionSiteCostAnalysis` flag. It should say
out loud that the work-item number does not appear in any commit subject, so the mapping is inferred
from the spec and the code, not read off the history.

---

## 17 — Are we on one version of everything? *(medium-hard, Security / compliance reviewer)*

> For a dependency review: across every `.csproj` and props file in the repository, is each NuGet
> package pinned to a single version, or do we have the same package at two different versions in
> different projects? Give me the total and name any divergence exactly.

**Tools**: `list_matches` (with `group`), `grep`, `glob`, `read_file`, `list_extensions`,
`file_history`.

**Why the direct arm struggles**: this is `list_matches` with a capture group — the indexed
equivalent of `grep -o … | sort | uniq -c` over every project file at once. The direct arm must Glob
the `.csproj` and `.props` files, Read each, parse the XML in its head and tally, and the failure mode
is not "wrong" but "sampled": it reads the ones it thinks matter and reports a clean bill of health.
Since the answer is a single divergence among sixty-eight, sampling misses it with high probability,
and a false "all consistent" is exactly the wrong answer to give a compliance reviewer.

**A good answer** reports roughly 68 distinct package/version pairs across the `.csproj` and
`Directory.Build.props` files, and identifies the one package that appears at two versions:
**`Microsoft.Extensions.ObjectPool` at `10.0.12` in `src/shared/CodeGeneratorLib/CodeGeneratorLib.csproj`
and at `10.0.11` in `src/shared/IML.Lib.Database/IML.Lib.Database.csproj`.** Bonus for noting that
the repository pins the SDK centrally in `global.json` (`10.0.100`, `rollForward: latestFeature`,
`allowPrerelease: false`) while package versions are per-project rather than centrally managed, which
is why divergence is possible at all, and for flagging that the frontend's dependencies live in
`pnpm-lock.yaml` files and were not part of this count.

---

## 18 — What does the pipeline enforce that I do not have to? *(medium, Security / compliance reviewer)*

> I review pull requests here. Tell me which quality and security rules are enforced automatically —
> what runs, on what trigger, and what it checks — so I stop reviewing things a machine already
> rejects.

**Tools**: `list_tree`, `glob`, `read_file`, `grep`, `list_declarations`, `find_definition`,
`file_history`.

**Why the direct arm struggles**: the answer is in three unrelated languages in three unrelated
places — YAML under `devops/pipelines`, C# ArchUnitNET tests under `src/Api/Architecture.Tests`, and a
generator check invoked as `dotnet run --project src\Model\Model.csproj check` that is documented only
in the README. There is no single word to grep for. The direct arm will find whichever it looks for
first and report that as the answer.

**A good answer** covers all three layers:
- **Pipelines** (`devops/pipelines/`): `pipeline-source-scan.yml` runs a SonarQube scan on a
  schedule, not on push — `trigger: none` plus two crons, `0 1 * * *` (nightly) and `0 12 * * *`
  (midday), on `master` only, with `always: false` so it skips when nothing changed. Separately
  `pipeline-build-check.yml`, `pipeline-test-backend.yml` and the E2E pipelines exist, and the
  deployment pipelines are split by environment (testing / staging / production) plus hotfix and
  redeploy variants.
- **Architecture tests** (`src/Api/Architecture.Tests/`): three hand-written ArchUnitNET rules —
  `CheckNoDirectCacheAccess`, `CheckNoIConfiguration` and `CheckRounding` (which forbids `Math.Round`
  outside a four-class whitelist, loaded against the `Host`, `Gateway`, `MigrationOrchestrator` and
  `ManagementSite` assemblies) — plus a `NugetWhitelists` folder with `CommonCoreProjectChecks` and
  `ProjectDependenciesChecker`.
- **The generator check**: `dotnet run --project src\Model\Model.csproj check` fails if any
  generator-managed file has been hand-edited or is missing — including `routes-lock.json`.

---

## 19 — Which build system is real? *(medium, New developer)*

> The README tells me to run `nuke` for everything. There are two build directories under `devops`.
> Which build system do the pipelines actually use, when did that change, and what should I run
> locally?

**Tools**: `list_tree`, `glob`, `grep`, `read_file`, `git_log`, `file_history`, `hot_files`,
`co_changed`.

**Why the direct arm struggles**: the two systems are near-identical by structure — `devops/Nuke`
has `Build.Compile.cs`, `Build.Deploy.cs`, `Build.Docker.cs`, `Build.Scan.cs`, `Build.Test.cs`;
`devops/Cake` has `BuildContext.Compile.cs`, `BuildContext.Deploy.cs`, `BuildContext.Docker.cs`,
`BuildContext.Scan.cs`, `BuildContext.Test.cs`. Both are committed, both compile, both have entry
scripts in `devops/`. Deciding which one is live requires reading the pipeline YAML and noticing the
*commented-out* lines, and deciding when it changed requires history the direct arm does not have.

**A good answer** states that the pipelines invoke **Cake**: every build/test/scan job calls
`./devops/cake-build.sh` with a `--target=…`, and the Nuke invocation is present directly above it,
commented out with an explicit "left commented for an easy revert" note (see
`devops/pipelines/pipeline-source-scan.yml` and `template-job-build-*.yml`). It should also flag the
documentation drift: the README still documents only `nuke` commands (`nuke InitializeLocalConfig`,
`nuke SetupSecrets`, `nuke Deploy`, `nuke Test`, `nuke ScanFrontend`, …), so a new developer following
it uses a system CI no longer runs. Using `git_log`/`hot_files` over `devops/` to date the switch, and
saying which of the two directories is still receiving commits, is the part the direct arm cannot
reach.

---

## 20 — What moves when the Model moves? *(hard, Product Manager)*

> If we change one entity definition in the `Model` app — say the Metric Computation — what else has
> to change with it? I want the real blast radius, including the parts that are generated, and I want
> to know how confident you are.

**Tools**: `co_changed`, `hot_files` (`depth`, `directory`), `who_imports`, `imports`,
`find_references`, `find_definition`, `list_declarations`, `git_log` (`path`), `read_file`.

**Why the direct arm struggles**: this is coupling that exists only in history and in a code
generator, and it is invisible in the import graph because the generator writes files nothing
*imports* from the Model — the dependency runs through a build step, not a `using`. `who_imports`
therefore under-reports and `co_changed` over-reports, and a good answer needs both plus the judgement
to say so. The direct arm has neither.

**A good answer** names the fan-out and ranks it: a change to
`src/Model/Modules/MasterData/MetricComputation/MetricComputationFeature.cs` (recorded under
`model/Model/Modules/MasterData/…` before the 2026-09-15 rename, so `co_changed` must be asked about
the historical path to see most of it) has historically travelled with, in order of weight,
`src/Frontend/modules/master-data`, the rest of `src/Model`, `src/Api/Modules/MasterData`,
`src/DocsPage/src/content` (the generated documentation site), `src/Frontend/modules/cross-functional`,
`src/Frontend/libs/form-kit`, `src/shared/CodeGeneratorLib` and `src/Frontend/shared/value-objects`.
It should explain *why* — the Model is the input to `CodeGeneratorLib`, which emits the `.g.cs` value
objects and API registrations on the backend, the `_g.tsx` descriptors and route registrations on the
frontend, the E2E feature-flag constants, the DocsPage content and `routes-lock.json` — and it should
carry the two caveats the tools state about themselves: `co_changed` is evidence and not proof, and
any commit that touched a great many paths at once (the Model move being the obvious one) is excluded
from pairing. Naming `docs/adr/0002-metric-computation-estimate-is-a-distinct-lookup-concept.md` as
prior art on exactly this entity is a strong bonus.

---

## The no-answer questions, at a glance

| # | Question | Kind of nothing | How it was verified |
|---|---|---|---|
| 5 | The German translation catalogue | A file that a config file implies exists | `git ls-files '*.po' '*.pot'` returns only `it.po`; the locales directory holds one file |
| 8 | The cronoprogramma / Gantt feature | A feature with plausible grep hits that are test fixtures | Two hits repo-wide, both `'Cronoprogramma lavori.xlsx'` mock filenames in Storybook stories; no entity, route or component |
| 12 | PR 39400 | A ticket number in the live range that was never used here | No `Merged PR 39400` subject in history; the only `39400` in the tree is inside a decimal in a Primus test XML and a byte match in a PDF |
| 13 | Davide Ricci's contributions | An author who never committed | Fourteen distinct author names in history, none of them Ricci; `davide` appears nowhere in `src` or `docs`, and the two `ricci` hits are a benchmark fixture surname and the Italian word `arriccio` |
| 14 | The diff of a commit | A thing the index structurally does not hold | The index records paths and line counts per commit, never content; `commit` and `commit_files` both say so; no tool returns historical file content |
| 15 | The `MMA/MceImportExcel` branch | A branch named in a committed spec, outside the index's scope | Only the default branch is imported; no branch, PR or review data exists in the index; the branch is absent from the clone |
| 7 | *(partial)* The BIM viewer's users | A real, complete library that nothing imports | `@edilverso/bim-viewer` referenced only from config, its own manifest and its own readme; absent from `modules.json` |

Question 7 is listed here because its correct answer is also a negative, but it is not a probe: the
library genuinely exists and the question is answerable. It is the control for the six that are not —
an agent that says "nothing here" to everything should fail 7, and an agent that always finds
something should fail the other six.

## Tool coverage

| Tool | Questions |
|---|---|
| `which_project` | — (assumed; set 1 covers it) |
| `repo_info` | 3 |
| `project_overview` | 3 |
| `list_tree` | 1, 2, 3, 7, 8, 11, 18, 19 |
| `list_extensions` | 3, 17 |
| `glob` | 1, 2, 3, 4, 5, 6, 7, 8, 9, 16, 17, 18, 19 |
| `read_file` | 1, 2, 4, 5, 6, 8, 9, 15, 16, 17, 18, 19, 20 |
| `list_declarations` | 7, 9, 18, 20 |
| `grep` | 1, 3, 4, 5, 6, 7, 8, 9, 12, 15, 16, 18, 19 |
| `list_matches` | 3, 6, 17 |
| `find_definition` | 4, 8, 9, 18, 20 |
| `find_references` | 4, 6, 7, 9, 16, 20 |
| `imports` | 7, 20 |
| `who_imports` | 7, 20 |
| `git_log` | 2, 10, 11, 12, 13, 15, 16, 19, 20 |
| `authors` | 10, 13 |
| `file_history` | 2, 4, 5, 6, 7, 9, 10, 14, 15, 16, 17, 18, 19 |
| `blame` | 14 |
| `hot_files` | 3, 10, 13, 19, 20 |
| `co_changed` | 19, 20 |
| `commit` | 10, 11, 12, 14, 15, 16 |
| `commit_files` | 10, 11, 12, 14, 16 |

`commit` and `commit_files` appear in no question of set 1; they carry questions 11, 12 and 16 here.
The three arguments that set 1 never reached — `authors(path=…)` / `git_log(path=…)` across a rename
(10, 20), `hot_files(exclude=…)` over generated output (3), and `list_matches(group=…)` (17) — are
each the pivot of the question they appear in. If the comparison has to be cut short, run 5, 8, 10,
12 and 17: two probes, the rename trap, the collision trap, and the one aggregation whose answer is a
single needle.
