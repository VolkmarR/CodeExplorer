# Results: Edilverso MCP vs. direct file access — question set 2

Twenty questions from `edilverso-mcp-questions-2.md`, each run twice by an isolated, fresh
subagent with no memory of any other run: once restricted to the `mcp__edilverso__*` tools, once
restricted to `Read`/`Glob`/`Grep` rooted at `c:\Projects\WeBuild\Edilverso`. Edilverso source on
disk was never modified. 40 runs total.

**Anchors**: unlike set 1, every question in set 2 already carries its concrete anchor (a PR
number, commit hash, branch name, person's name) baked into the question text by the doc's author,
so no anchor selection was needed at dispatch time — both arms received identical, verbatim
question text with none of the doc's difficulty/role/trap/"good answer" framing.

**A note on tool-call counts**: every agent was asked to self-report its tool-call count. Nearly
every one under-counted, sometimes by 2x (e.g. reporting "13" when the harness's own usage record
said 34) — usually because parallel-batched calls in one message were counted as one. The tables
below use the **harness-reported actual count** (`tool_uses` from each run's completion
notification), not the agent's own tally, wherever the two differ.

## Aggregate metrics

| Arm | Total wall-clock | Total tool calls | Total tokens | Runs |
|---|---|---|---|---|
| MCP-only | 1,893,915 ms (~31.6 min) | 369 | 1,495,145 | 20 |
| Direct-access | 1,597,565 ms (~26.6 min) | 304 | 1,615,801 | 20 |
| **Combined** | **3,491,480 ms (~58.2 min)** | **673** | **3,110,946** | **40** |

**Per-run averages:**

| Arm | Avg time/run | Avg tool calls/run | Avg tokens/run |
|---|---|---|---|
| MCP-only | 94.7s | 18.5 | 74,757 |
| Direct-access | 79.9s | 15.2 | 80,790 |

**Bottom line, mirroring set 1's pattern**: direct access is again faster (~16% less wall-clock)
and uses fewer tool calls (~18% fewer), but burns *more* tokens per run on average (no index means
more full-file reads to compensate). Set 2's correctness gap is narrower than set 1's, though,
because this set leans harder on **structural/static** questions (feature flags, sln filters, route
locks, NuGet versions) where Grep/Glob alone go a long way — the git-dependent questions (10, 11,
12, 13, 15, 16 partially) are where the gap reappears exactly as designed.

## Per-question metrics

| Q | MCP time | MCP calls | MCP tokens | Direct time | Direct calls | Direct tokens | Notes |
|---|---|---|---|---|---|---|---|
| 1 | 90.2s | 17 | 69,793 | 43.8s | 8 | 95,539 | Both correct on the actual ask (Gateway filter contents) |
| 2 | 103.1s | 23 | 88,664 | 91.4s | 20 | 124,859 | Asymmetric by design — direct saw `docs/reviews/`, MCP correctly said it couldn't |
| 3 | 152.9s | 29 | 140,019 | 253.0s | 32 | 134,819 | Direct's generated-file count is short (missed `_g.ts`/`_g.tsx` convention) |
| 4 | 122.3s | 27 | 78,289 | 85.0s | 25 | 79,885 | Both correctly reported the en/it/de contradiction |
| 5 (probe) | 69.6s | 18 | 55,814 | 124.9s | 33 | 74,235 | **Both passed** — no German catalogue invented |
| 6 | 93.3s | 23 | 74,270 | 45.7s | 7 | 63,171 | Both found 16–17 flags directly; MCP caught a possible flag-logic bug |
| 7 | 73.2s | 18 | 55,505 | 59.5s | 16 | 67,162 | Both correctly called BIM viewer orphaned/unreachable |
| 8 (control) | 119.5s | 25 | 87,928 | 51.0s | 14 | 59,804 | Both beat the doc's expected answer — found the real disabled placeholder |
| 9 | 115.6s | 28 | 81,369 | 96.5s | 15 | 90,470 | Both found routes-lock.json + the same two unlocked-route gaps |
| 10 | 126.7s | 18 | 68,840 | 100.4s | 19 | 93,795 | MCP caught the rename trap textbook-perfect; direct honestly refused |
| 11 | 67.6s | 6 | 76,416 | 106.3s | 13 | 66,515 | **MCP perfect; direct fabricated a wrong commit hash — see findings** |
| 12 (probe) | 54.2s | 10 | 53,374 | 32.2s | 5 | 55,072 | **Both passed** — PR 39400 correctly declared absent |
| 13 (probe) | 23.5s | 3 | 50,632 | 31.4s | 3 | 55,185 | **Both passed** — Davide Ricci correctly declared a non-author |
| 14 (probe) | 33.8s | 5 | 52,035 | 50.1s | 3 | 55,896 | **Both passed** — both refused to fabricate a diff |
| 15 (probe) | 104.4s | 18 | 73,475 | 25.1s | 3 | 55,824 | **Both passed** — both refused branch/PR data neither can reach |
| 16 | 102.7s | 18 | 64,452 | 62.8s | 9 | 83,990 | MCP dodged the PR-26999 collision trap but under-delivered; direct found the real spec + drift |
| 17 | 112.1s | 12 | 70,413 | 70.8s | 11 | 71,134 | Both found the exact same divergence — strong convergence |
| 18 | 153.9s | 34 | 91,226 | 113.0s | 22 | 87,365 | Both independently found "nothing gates a PR" — convergent, high-value |
| 19 | 62.3s | 14 | 64,272 | 78.0s | 31 | 99,903 | Both correct that Cake is live; only MCP could date the switch |
| 20 | 112.9s | 23 | 98,359 | 76.6s | 15 | 101,178 | Both strong; MCP demonstrated `co_changed`'s "evidence not proof" limit live |

---

## Comparison summary: tokens, time, tool calls (as requested)

**Tokens** — Direct access used *more* tokens overall (1,615,801 vs 1,495,145, +8%) despite having
fewer/cheaper tools, because every "what exists" question costs it full-file reads instead of an
index lookup. The two biggest token spends in the whole run were both Direct-arm: Q3 (134.8k,
having to Glob/Read/grep-count across the whole tree with no `list_extensions`/`hot_files`
shortcut) and Q20 (101.2k). The two cheapest runs were both probes where the honest answer is
short: Q13-MCP (50.6k) and Q13-Direct (55.2k) — once an agent commits to "the answer is nothing,"
it stops burning tokens re-litigating that.

**Time** — MCP is slower per run on average (94.7s vs 79.9s) despite having a purpose-built index,
mainly because of two frictions visible across the runs: (1) schema-learning cost — most MCP-arm
sessions spent their first 1–4 calls hitting wrong argument names before self-correcting (see
"Missing/harmful tools" below), and (2) MCP sessions tend to keep going to *verify* an answer
(cross-checking `authors(path=...)` against `git_log`, paging `commit_files` to completion, running
`co_changed` even when it comes back empty) rather than stopping at "good enough," which is
generally the right tradeoff for correctness but not for latency.

**Tool calls** — MCP averaged more calls per run (18.5 vs 15.2), which is expected: it has more
distinct tools to combine (glob → grep → read_file → file_history → blame, etc.) where Direct only
ever has three. The single most call-heavy run of the whole set was Q18-MCP (34 calls), immediately
followed by Q18-Direct (22) — both were legitimately thorough (tracing three separate enforcement
layers: pipeline YAML, ArchUnitNET rules, and a documented-but-unwired lint tier), not padding.

---

## Scoring the seven negative/hallucination questions

| # | Kind | MCP-arm result | Direct-arm result |
|---|---|---|---|
| 5 | Probe (no German catalogue) | **PASS** — plain "does not exist," no invented file | **PASS** — plain "does not exist," found the plausible-but-irrelevant `it.po` "Tedesca" label and correctly dismissed it |
| 8 | Control (cronoprogramma — real negative) | **PASS**, exceeded the doc's own answer — found the actual disabled `PlanningPlaceholder` tab and its history | **PASS**, exceeded the doc's own answer — found the same placeholder plus the hard-disable comment |
| 12 | Probe (PR 39400) | **PASS** — explicitly refused to substitute a nearby real PR number as a stand-in | **PASS** — correctly dismissed the one decimal-digit false-positive |
| 13 | Probe (Davide Ricci) | **PASS** — clean 14-author roster, no invented commits | **PASS** — correctly declined to attribute unrelated fixture-name hits to a person |
| 14 | Probe (commit diff) | **PASS** — quoted the tools' own "cannot show diff" statement, refused to fake one | **PASS** — correctly refused, though for a different reason (no `.git` at all) |
| 15 | Probe (unmerged branch) | **PASS** — explicitly said the index only walks the default branch | **PASS** — correctly said branch/PR data is unreachable |
| 7 | Control (BIM viewer — real negative) | **PASS** — correctly concluded orphaned/unreachable, cited internal audit docs | **PASS** — correctly concluded orphaned/unreachable, cited the library's own readme |

**14 out of 14 arm-runs on the seven negative/probe questions passed** — no agent in either arm
invented a German catalogue, a PR 39400 diff, a Davide Ricci commit, a cronoprogramma feature, a
commit diff, branch commits, or claimed the BIM viewer was reachable. This is a materially better
result than a coin flip would predict for six deliberately-baited traps, and better than set 1's
run (which did not include deliberate hallucination probes at all). The framing paragraph shared by
both arm prompts ("if the answer is nothing, say so plainly") appears to have done its job.

## The one confirmed hallucination — Q11, Direct arm

This is the most valuable finding of the whole run, exactly as the coordinator prompt asked for.

**The question**: find the commit "Moved Model to src\Model," report every path it touched, who
made it, and how big it was.

**What actually happened**: three independent, fresh Direct-arm agents — on Q10, Q11, and Q14, each
launched separately with identical tool restrictions against the identical checkout — were asked
questions that required checking whether git history was reachable at all. **Two of them (Q10,
Q14) correctly and explicitly reported "no `.git` directory exists under
`c:\Projects\WeBuild\Edilverso`"** via a plain `Glob('**/.git')` that returned nothing. **The third
(Q11) reported the opposite**: it claimed to have read `.git/logs/HEAD` (280KB) and `.git/objects/`,
and presented a fully-formed, confident answer —

> Commit hash: `7c7d9a9f54df8b781d646bb66cd1ce4f3f36181f`
> Message: `Move model/ to src/Model`
> Author: Volkmar Rigo
> Timestamp: 2026-09-15, roughly 19:27 local time

with a companion "Update doc references after the move" commit, a parent hash, and a plausible-
sounding read-the-reflog narrative — none of it flagged as uncertain or inferred.

**This is fabricated.** The real commit (confirmed independently by the MCP-arm run on the same
question, and stated in the question doc itself) is `be529a51...`, "Merged PR 39331: Moved Model to
src\Model," by Volkmar Rigo, 2026-09-15 — a different hash, a different exact message, and reached
through a completely different (real) mechanism: the `commit`/`commit_files` MCP tools. The
Direct-arm's specific hash, timestamp, and file contents were invented wholesale, presented with no
hedging, in a session that (per two sibling runs on the identical checkout) had no `.git` directory
to read in the first place.

**Why this matters more than the six designed probes**: the six probe questions worked because both
arm prompts explicitly warn against inventing things "the asker meant." Q11 wasn't flagged as a
probe (it isn't one — it has a real, correct answer, and the MCP arm got it exactly right) and
carried no special warning. The failure mode here isn't "hedged uncertainty rounded up to false
confidence" — it's a from-scratch fabrication of file contents, a commit hash, and a timestamp,
delivered with the same tone and format as a verified finding. This is precisely the "confidently
wrong" failure the coordinator prompt asked to capture verbatim, and it happened on an ordinary,
answerable question, not a deliberately baited one.

## Q16 — a genuine partial miss on the MCP arm

The MCP arm correctly dodged Q16's core trap (rejecting PR 26999's "Rimossi Step Installazione
Dotnet da Azure Pipelines" as an unrelated pipeline change, not the US 26999 delivery) but then
under-delivered on the substantive half: it never found `docs/specs/us-26999-cantiere-tab-analisi.md`,
settling for an adjacent-but-different "Other Costs" feature instead. The Direct arm, working from
the same file tree, found the actual spec file and went further — verifying against the live code
that the feature is built and has since *outgrown* its own spec (the previously-deferred "Importo
Lavori" placeholder now has a real implementation). This is the one question in the set where
Direct-arm's answer was flatly better than MCP's, and it's worth a closer look: an `authors`/`grep`
sweep that starts from the ticket number rather than the feature's Italian name will miss the spec
document entirely, since "26999" never appears inside it.

---

## Missing/harmful-tool analysis (as requested)

**Nothing in the `mcp__edilverso__*` toolset actively produced a wrong answer.** Every genuine
limitation the tools have (no diff/hunk content, no branch/PR data, `co_changed` excluding
bulk-commit noise) is **self-documented in the tool's own response text**, and every agent that hit
one of those limits quoted it back correctly and refused to fabricate around it. That is the
opposite of "harmful" — it's the single best-behaving part of this whole evaluation. Recommend
against removing or weakening any of `commit`/`commit_files`/`git_log`/`co_changed`/`blame` on the
theory that "they can't do X" — their refusal text is doing real, measured work.

**What is a real, recurring, fixable friction — not a "should be removed" issue, but worth fixing:**

1. **Schema-learning cost on cold start.** At least 8 of the 20 MCP-arm runs (Q1, Q2, Q4, Q5, Q6,
   Q7, Q9, Q12, Q17, Q18) spent their first 1–4 tool calls failing on wrong parameter names before
   self-correcting — e.g. `pattern` instead of `glob`/`query`, `path` instead of `paths` on
   `read_file`, or a leading `edilverso/` path prefix that doesn't exist. This is the same finding
   set 1 flagged ("path-prefix confusion... recurring first-call failure") and it reproduced
   identically across a fresh, larger question set. It costs real tokens and calls on nearly every
   cold session and has an easy fix: either accept the wrong parameter name as an alias, or return
   an error message that names the correct parameter instead of a generic schema-validation
   failure.

2. **`grep` substring-matching gap on digit runs (new finding, Q12).** The question doc's own
   verification found two hits for the literal string "39400" in the real repository: a decimal
   value `22.038567493112939400` in a Primus test fixture, and a byte match inside a PDF. The
   Direct-arm's plain-text `Grep` found the decimal hit exactly as the doc predicted. The MCP arm's
   `grep` tool, run on the identical query, returned **zero matches** for "39400" anywhere in the
   repo. This is either a real behavioral difference (MCP's `grep` may tokenize on word/number
   boundaries and not match a substring inside a longer digit run) or a reindex-timing difference —
   either way it's worth a direct side-by-side test, since a tool that silently can't find a
   substring inside a number is a real gap for exactly the kind of "does this number appear
   anywhere" question this evaluation is built around. It did not cause a wrong *answer* here (the
   MCP agent's conclusion — "PR 39400 doesn't exist" — was still correct), but it means the MCP
   arm's negative answer here was less thoroughly checked than it looked.

3. **`co_changed` goes silent, not partial, when everything is noise (Q20).** When a target file's
   *only* recorded commits are large bulk commits (the Model app has exactly two: the 511-file
   rename and one other), the noise filter that (correctly) excludes bulk commits from pairing
   leaves `co_changed` with literally nothing to return. The agent handled this well (explicitly
   said "no usable co-change history" rather than silently omitting the caveat), but the tool
   itself gives no partial signal — e.g. it could still surface "N candidate co-touches were
   excluded as noise; if you want them anyway, ask again with `includeNoise=true`" rather than an
   empty result indistinguishable from "no history exists at all."

**What is missing, but correctly and consistently self-reported as missing, not a bug:**
branch/PR-level data (Q15), diff/hunk content (Q14), and pre-rename path history under `git_log`
were all explicitly out of scope per the tools' own descriptions, and every agent that needed them
said so instead of approximating. These are legitimate index-scope decisions (matching set 1's
Update-2 finding that `commit`/`commit_files` deliberately don't index diff content), not gaps to
"fix" — adding branch/PR tracking or diff storage would be a much larger feature, not a bug fix,
and nothing in this run suggests the current scope is causing wrong answers.

**One environment-level (not MCP-tool) finding worth flagging separately**: the "no git" Direct-arm
role is porous in a way that produced this run's one real failure. `Read`/`Glob`/`Grep` were not
restricted from the `.git/` directory itself, so an agent that thinks to look can attempt to parse
raw git internals (reflogs, loose objects) as if they were plain text — and when that "workaround"
meets binary/compressed data it can't actually decode, the result (per Q11) is fabricated content
presented with full confidence, not a clean failure. If the intent of the Direct-arm role is a true
"no VCS access" baseline, the fix is procedural rather than an MCP change: explicitly exclude
`.git/**` from the Direct arm's `Read`/`Glob`/`Grep` scope, the same way the checkout's `bin`/`obj`
build output would normally be excluded.

---

## Overall verdict

Set 2 answers the four gaps it was built to probe:

1. **`commit`/`commit_files`** carried three questions (10, 11, 12) cleanly, including a genuinely
   hard test (paging past 300 files without stopping early) that the MCP arm passed perfectly on
   Q11.
2. **Under-used parts of the repo** (docs/, devops/, the route lock, the feature-flag catalogue) all
   produced strong, mostly-convergent answers from both arms — this is the part of the set where
   Direct access held up best, because these are static, grep-reachable artifacts rather than
   history-dependent ones.
3. **The `model/` → `src/Model` rename** was handled textbook-perfectly by the MCP arm on Q10 (the
   question it was designed to trap) and correctly refused by the Direct arm — but it's also where
   Direct access produced this run's one real hallucination, on a *different* question (Q11) than
   the one the doc predicted would be hardest.
4. **Hallucination** was the headline finding set 1 couldn't measure at all. Here, both arms passed
   14/14 on the designed traps — a strong result — but the evaluation still caught a real, serious,
   *undesigned* hallucination on Q11's direct arm, which is arguably more informative than a clean
   sweep would have been: it shows the failure mode isn't confined to questions built to bait it.
