# Question set: the MCP against direct directory access — Edilverso

Ten questions for measuring what the CodeExplorer MCP endpoint gives a non-developer that a coding
agent with a file system does not, put to the **Edilverso** project. Same method as
`mcp-vs-direct-access.md` (the Radix set), aimed at a project that is the opposite shape: a small
TypeScript/C# mono-repo instead of a huge single-language X# one, so any gap that is really about
"only works for X#" should show up here as *not* a gap.

Edilverso is indexed by the server; its source is also on disk at `c:\Projects\WeBuild\Edilverso`,
so the same question can be put to an agent with nothing but `Read`, `Glob` and `Grep`.

8,329 git-tracked files: 2,763 `.ts`, 2,693 `.cs`, 1,334 `.tsx`, plus `.md`, `.json`, `.csproj`,
`.sql`. It is a mono-repo of separate apps — `Api`, `Frontend`, `ManagementSite`, `Model`,
`MigrationOrchestrator`, `TenantSchemaImporter`, `DocsPage`, `LandingPage`, `E2E`, `shared` — under
`src`. History is Azure DevOps "Merged PR NNNNN" commits, no Todo/WorkItem numbers, and commit
messages and domain vocabulary mix English and Italian (`appalto pubblico`, `fattura acconto`,
`Computo Metrico`).

## How to run the comparison

Same as the Radix set: ask each question twice in a fresh session, MCP-only vs. `Read`/`Glob`/
`Grep`-only rooted at `c:\Projects\WeBuild\Edilverso`. Record wall clock, number of tool calls,
tokens, whether the answer was right, and whether it was reached at all or the agent gave up,
truncated, or guessed. Note anything confidently wrong.

## The roles

**Product Manager** — works out what a feature does today, what changing it would cost, and where
the product is actually moving. Needs breadth and blast radius, not code literacy.

**Helpdesk employee** — has a customer on the line. Needs to know which app/module a symptom
belongs to, who implemented it, when, and under which PR, fast enough to answer while the customer
waits.

---

## 1 — What is this product, in one answer? *(easy, Product Manager)*

> I have just taken over Edilverso as product manager and have never seen the code. Give me an
> orientation: how many repositories, how big, what languages is it written in, what are the
> top-level apps, and how far back does its history go. Tell me which parts are big enough to
> matter and which are rounding errors.

**Tools**: `which_project`, `repo_info`, `project_overview`, `list_tree`, `list_extensions`.

**Why the direct arm struggles**: a mono-repo mixes a TS frontend, a C# API, and several smaller
apps under one `src`; the direct arm has to walk all of them and guess which counts as "the
product" versus tooling/scripts/E2E.

**A good answer** separates `Api`/`Model` (C#) from `Frontend`/`ManagementSite`/`DocsPage`/
`LandingPage` (TS/TSX), names the small apps as small, and gives the indexed commit.

---

## 2 — Who builds this? *(easy, Helpdesk)*

> A customer wants to know who maintains the product. Who has committed to Edilverso, who is most
> active, and what has been going on in the last few weeks?

**Tools**: `authors`, `git_log`.

**Why the direct arm struggles**: same as Radix — shelling out to `git` and aggregating by hand.

**A good answer** ranks people by commits with the address each commits from.

---

## 3 — When did this app last change? *(easy, Helpdesk)*

> A customer is on a version of the tenant management site and asks whether anything has changed
> in it recently. Find the `ManagementSite` app, and tell me when it was last touched, by whom, and
> under which PR.

**Tools**: `glob`, `list_tree`, `file_history`, `git_log`.

**Why the direct arm struggles**: identifying which files belong to `ManagementSite` versus
`shared` code it imports takes exploration; per-file history is one `git log` per candidate.

**A good answer** names the file(s), date, person, and the PR number from the merge commit.

---

## 4 — Where is the product actually moving? *(medium, Product Manager)*

> For the last six months of history: which apps are under active development and which have not
> been touched at all? I want to know where the engineering effort is really going, not where the
> code volume is.

**Tools**: `hot_files`, `git_log`, `list_tree`, `repo_info`.

**Why the direct arm struggles**: ranking over the whole history window is aggregation the direct
arm has to invent; it will sample instead and produce an answer that looks confident and is not
representative.

**A good answer** separates churn from size across the *apps*, not just files, and states the
window.

---

## 5 — A customer quoted a message back to us *(medium, Helpdesk)*

> A customer sent a screenshot with an error or label in it (pick a real string — Italian or
> English — from the frontend or API for the actual run, and use the same one in both arms). Find
> where that text comes from in the code, which app it belongs to, who last changed that line and
> when, and whether the wording has been edited since it was first written.

**Tools**: `grep`, `read_file`, `blame`, `file_history`.

**Why the direct arm struggles**: the string may live in a `.ts`/`.tsx` component, an i18n
resource, or a C# validation message — three different places to search — and going from a hit to
"who last changed *that line*" needs `git blame` on a specific range.

**A good answer** goes from string to file to line to commit to person, and says whether it was
ever reworded.

---

## 6 — How does this feature work today? *(medium, Product Manager)*

> Explain the current state of the "Computo Metrico" (quantity takeoff) import from PDF: where it
> lives, what the main entry points are, what each of those files declares, and what they depend
> on. I need to describe it to a customer without reading the code myself.

**Tools**: `grep`, `find_definition`, `list_declarations`, `read_file`, `imports`.

**Why the direct arm struggles**: the feature likely spans a C# service and a TS UI; the direct arm
must read whole files to learn what they declare on both sides of that boundary.

**A good answer** gives an outline per file, not a code dump, and is explicit about the
frontend/backend split.

---

## 7 — What shipped under this PR? *(medium-hard, Helpdesk)*

> A customer refers to a change we delivered under a PR number. Find the commit that carries that
> number, tell me exactly which files it touched, what the change was in plain language, who did
> it, and whether anything has been changed in those files since.

Pick a real PR number from the history for the run (e.g. one of the `Merged PR NNNNN` commits).

**Tools**: `git_log`, `file_history`, `blame`, `read_file`, `list_declarations`.

**Why the direct arm struggles**: searching commit *messages* rather than code, then pivoting to
per-file history, is several `git` invocations with formats to get right.

**A good answer** distinguishes "this is what that PR changed" from "this is the state of those
files now".

---

## 8 — What breaks if we change this? *(hard, Product Manager)*

> We are considering changing a shared routine used across apps — pick one that is genuinely
> widely used from `shared` or `Model`, and tell me: everywhere it is called, which apps those
> calls sit in, what else normally has to change when that file changes, and what else depends on
> the file it lives in. Give me a rough size for the change and say what you are unsure about.

**Tools**: `find_references`, `who_imports`, `co_changed`, `grep`, `find_definition`.

**Why the direct arm struggles**: `who_imports` is the reverse of every import resolved at index
time, across both TS `import` and C# `using`; `co_changed` is coupling visible only in history. A
grep finds calls, not coupling, and cannot label a call with the enclosing `Type.Member`/component.

**A good answer** separates direct callers, import dependents, and historical co-change, and
treats the last as evidence rather than proof.

---

## 9 — How consistent are we? *(hard, Product Manager)*

> I want to know how much variation there is in how one thing is done across Edilverso — for
> example how many distinct spellings of a common API-call helper, error-handling pattern, or
> naming prefix are in use, how often each occurs, and in how many files. Then tell me whether this
> is one convention with a few stragglers or genuinely fragmented.

**Tools**: `list_matches`, `grep`, `glob`, `list_extensions`, `find_definition`.

**Why the direct arm struggles**: `list_matches` is the indexed equivalent of
`grep -o … | sort | uniq -c` across the whole repo. The direct arm either samples or floods its own
context.

**A good answer** gives distinct values with frequencies and file counts, and draws the
one-convention-versus-fragmented conclusion from the distribution.

---

## 10 — The full dossier *(hardest, both roles)*

> Build me a briefing on one feature area of Edilverso (pick one: an `Api`/`Model` domain area, or
> a `Frontend`/`ManagementSite` feature) that I can hand to both a product manager and a helpdesk
> colleague. It should cover: what the feature does and where it lives; its current structure and
> entry points; who has owned it historically and who touched it most recently; the PRs it has
> changed under; what else changes when it changes; everything that depends on it; and an honest
> estimate of what a moderate change would cost, with the things you are uncertain about listed
> separately.

**Tools**: nearly all of them — `project_overview`, `list_tree`, `glob`, `grep`, `find_definition`,
`list_declarations`, `read_file`, `imports`, `who_imports`, `find_references`, `co_changed`,
`hot_files`, `file_history`, `git_log`, `authors`, `blame`.

**Why the direct arm struggles**: the failure here is usually an exhausted context, not a wrong
fact. Reading a mono-repo at this breadth across both TS and C# does not fit; it will either
truncate silently or answer about the part it happened to read first.

**A good answer** is a briefing, not a transcript, and its uncertainty section is real.

---

## Tool coverage

| Tool | Questions |
|---|---|
| `which_project` | 1 |
| `repo_info` | 1, 4 |
| `project_overview` | 1, 10 |
| `list_tree` | 1, 3, 4, 10 |
| `list_extensions` | 1, 9 |
| `glob` | 3, 9, 10 |
| `list_declarations` | 6, 7, 10 |
| `read_file` | 5, 6, 7, 10 |
| `grep` | 5, 6, 8, 9, 10 |
| `list_matches` | 9 |
| `find_definition` | 6, 8, 9, 10 |
| `find_references` | 8, 10 |
| `imports` | 6, 10 |
| `who_imports` | 8, 10 |
| `git_log` | 2, 3, 4, 7, 10 |
| `authors` | 2, 10 |
| `file_history` | 3, 5, 7, 10 |
| `blame` | 5, 7, 10 |
| `hot_files` | 4, 10 |
| `co_changed` | 8, 10 |

As with Radix, `who_imports`, `co_changed`, `list_matches` and `list_declarations` have no
direct-access equivalent — but here `imports`/`who_imports` must resolve *two* import styles (TS
`import`, C# `using`), which is the one thing this project can test that Radix cannot.
