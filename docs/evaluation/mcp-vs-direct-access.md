# Question set: the MCP against direct directory access

Ten questions for measuring what the CodeExplorer MCP endpoint gives a non-developer that a coding
agent with a file system does not. They are asked of the **Radix** project, which the server already
indexes, and whose source is also on disk at `c:\Projects\XSharp\Radix` so the same question can be
put to an agent with nothing but `Read`, `Glob` and `Grep`.

Radix is the hard case on purpose: 40,962 files, of which 29,235 are X# `.prg`, plus 5,590 `.rc` and
5,553 `.xsfrm`. Its modules are named `RX…` with a letter for the tier, its history is Azure DevOps
merge commits carrying `Todo` and `WorkItem` numbers, and its domain vocabulary is German. None of
that is guessable from the outside, which is the point.

## How to run the comparison

Ask each question twice, in a fresh session each time.

- **MCP arm**: only the project's MCP endpoint. No file system access to Radix.
- **Direct arm**: only `Read`, `Glob`, `Grep` and a shell, rooted at `c:\Projects\XSharp\Radix`. No
  MCP.

Record, for each arm: wall clock, number of tool calls, tokens, whether the answer was right, and —
the one that matters most for these two roles — whether the answer was *reached at all* or the
agent gave up, truncated, or guessed. Note also anything the agent got confidently wrong, which is
worse than a miss for both roles.

A question is not a failure for the direct arm because it takes longer. It is a failure when the
answer is unobtainable, incomplete without saying so, or wrong.

## The roles

**Product Manager** — works out what a feature does today, what changing it would cost, and where
the product is actually moving. Needs breadth, counts and blast radius. Cannot read X#.

**Helpdesk employee** — has a customer on the line. Needs to know which module a symptom belongs to,
who implemented it, when, and under which ticket, fast enough to answer while the customer waits.

---

## 1 — What is this product, in one answer? *(easy, Product Manager)*

> I have just taken over Radix as product manager and have never seen the code. Give me an
> orientation: how many repositories, how big, what is it written in, what are the top-level parts,
> and how far back does its history go. Tell me which parts are big enough to matter and which are
> rounding errors.

**Tools this should reach for**: `which_project`, `repo_info`, `project_overview`, `list_tree`,
`list_extensions`.

**Why the direct arm struggles**: counting 40,962 files by language means walking the tree and
tallying extensions, and the agent has to decide for itself that `.xsfrm` and `.rc` are worth
naming. `repo_info` also states the commit the index stands at, which a directory cannot.

**A good answer** names X# `.prg` as the overwhelming bulk, distinguishes the form and resource
files from the code, and gives the top level without pasting a 40,000-line tree.

---

## 2 — Who builds this? *(easy, Helpdesk)*

> A customer wants to know who maintains the product. Who has committed to Radix, who is most
> active, and what has been going on in the last few weeks?

**Tools**: `authors`, `git_log`.

**Why the direct arm struggles**: it must shell out to `git`, and then decide how to aggregate. The
MCP answers with the commit addresses already ranked, which is also what makes a follow-up filter on
`git_log` work first time rather than after two wrong guesses at a name spelling.

**A good answer** ranks people by commits and gives the address each commits from, not just a
display name.

---

## 3 — When did this module last change? *(easy, Helpdesk)*

> A customer is on a version of the sales module and asks whether anything has changed in it
> recently. Find the sales-related `RX…` modules, and for the most active one tell me when it was
> last touched, by whom, and under which ticket.

**Tools**: `glob`, `list_tree`, `file_history`, `git_log`.

**Why the direct arm struggles**: the `RX…` naming has to be discovered before it can be searched,
and per-file history means one `git log` per candidate.

**A good answer** names the module, the date, the person and the `Todo`/`WorkItem` number from the
merge commit — the last of which is what the helpdesk actually pastes into the ticket.

---

## 4 — Where is the product actually moving? *(medium, Product Manager)*

> For the last six months of history: which parts of Radix are under active development and which
> have not been touched at all? I want to know where the engineering effort is really going, not
> where the code volume is.

**Tools**: `hot_files`, `git_log`, `list_tree`, `repo_info`.

**Why the direct arm struggles**: this is a ranking over the whole history window, not a lookup. The
direct arm has to invent the aggregation and will usually sample instead, producing an answer that
looks confident and is not representative.

**A good answer** separates churn from size, gives a window that is stated rather than assumed, and
notes that the window ends at the indexed commit rather than at today.

---

## 5 — A customer quoted a message back to us *(medium, Helpdesk)*

> A customer sent a screenshot with a German error message in it. Find where that text comes from in
> the code, tell me which module it belongs to, who last changed that line and when, and whether the
> wording has been edited since it was first written.

Pick a real string from the product for the actual run — a message or label — and use the same one
in both arms.

**Tools**: `grep`, `read_file`, `blame`, `file_history`.

**Why the direct arm struggles**: it can grep, but encoding and the `.rc`/`.xsfrm` files complicate
it, and going from a hit to "who last changed *that line*" needs `git blame` on a specific range.

**A good answer** goes from the string to the file, to the line, to the commit and the person, and
says whether the line was ever reworded — which is what decides whether the customer is on an old
build or hit a real bug.

---

## 6 — How does this feature work today? *(medium, Product Manager)*

> Explain the current state of the contract invoicing feature (`Vertragsfakturierung`): where it
> lives, what the main entry points are, what each of those files declares, and what they depend on.
> I need to describe it to a customer without reading X# myself.

**Tools**: `grep`, `find_definition`, `list_declarations`, `read_file`, `imports`.

**Why the direct arm struggles**: `list_declarations` has no equivalent — the direct arm must read
whole `.prg` files to learn what they declare, and X# declaration forms are not what a general agent
expects. Import resolution is worse: X# `.prg` has no `using` graph a generic tool understands.

**A good answer** gives an outline per file rather than a code dump, and is explicit about what it
could not resolve instead of inventing structure.

---

## 7 — What shipped under this ticket? *(medium-hard, Helpdesk)*

> A customer refers to a change we delivered under a Todo number. Find the commit that carries that
> number, tell me exactly which files it touched, what the change was in plain language, who did it,
> and whether anything has been changed in those files since.

Pick a real `Todo` / `WorkItem` number from the history for the run.

**Tools**: `git_log`, `file_history`, `blame`, `read_file`, `list_declarations`.

**Why the direct arm struggles**: searching commit *messages* rather than code, then pivoting from a
commit to per-file history, is several `git` invocations with formats the agent has to get right.

**A good answer** distinguishes "this is what that ticket changed" from "this is the state of those
files now", which is precisely the distinction a customer question turns on.

---

## 8 — What breaks if we change this? *(hard, Product Manager)*

> We are considering changing a shared routine used across modules. Pick one that is genuinely
> widely used, and tell me: everywhere it is called, which modules those calls sit in, what else
> normally has to change when that file changes, and what else depends on the file it lives in.
> Give me a rough size for the change and say what you are unsure about.

**Tools**: `find_references`, `who_imports`, `co_changed`, `grep`, `find_definition`.

**Why the direct arm struggles**: this is the question a directory cannot answer. `who_imports` is
the reverse of every import line in the project, resolved at index time; `co_changed` is coupling
that exists only in the history and is invisible in the code. A grep for the name finds the calls
and not the coupling, and cannot label a call with the `Type.Member` it sits in.

**A good answer** separates the three kinds of blast radius — direct callers, import dependents,
historical co-change — and treats the last as evidence rather than proof.

---

## 9 — How consistent are we? *(hard, Product Manager)*

> I want to know how much variation there is in how one thing is done across Radix — for example
> how many distinct spellings of a common prefix, message helper or base class are in use, how often
> each occurs, and in how many files. Then tell me whether this is one convention with a few
> stragglers or genuinely fragmented.

**Tools**: `list_matches`, `grep`, `glob`, `list_extensions`, `find_definition`.

**Why the direct arm struggles**: `list_matches` is the indexed equivalent of
`grep -o … | sort | uniq -c` across 9.5 million lines. The direct arm either samples or floods its
own context; either way the counts it reports are not the counts.

**A good answer** gives distinct values with frequencies and file counts, and draws the
one-convention-versus-fragmented conclusion from the distribution rather than asserting it.

---

## 10 — The full dossier *(hardest, both roles)*

> Build me a briefing on one feature area of Radix that I can hand to both a product manager and a
> helpdesk colleague. It should cover: what the feature does and where it lives; its current
> structure and entry points; who has owned it historically and who touched it most recently; the
> tickets it has changed under; what else changes when it changes; everything that depends on it; and
> an honest estimate of what a moderate change would cost, with the things you are uncertain about
> listed separately.

**Tools**: nearly all of them — `project_overview`, `list_tree`, `glob`, `grep`, `find_definition`,
`list_declarations`, `read_file`, `imports`, `who_imports`, `find_references`, `co_changed`,
`hot_files`, `file_history`, `git_log`, `authors`, `blame`.

**Why the direct arm struggles**: the failure here is usually not a wrong fact but an exhausted
context. The direct arm has to read to learn anything, and reading Radix at this breadth does not
fit; it will either truncate silently or answer about the part it happened to read first.

**A good answer** is a briefing, not a transcript, and its uncertainty section is real — the test is
whether the agent knows what it did not establish.

---

## Tool coverage

Every tool the endpoint exposes is exercised by at least one question.

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

The four that have no direct-access equivalent at all — `who_imports`, `co_changed`, `list_matches`
and `list_declarations` — are concentrated in questions 6, 8 and 9. If the comparison is to be cut
short, those three are the ones to run.
