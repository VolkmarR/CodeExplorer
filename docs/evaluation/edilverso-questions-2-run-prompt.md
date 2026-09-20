# Run prompts: question set 2, Edilverso

The prompts for running `edilverso-mcp-questions-2.md` as a two-arm comparison. One coordinator
prompt, two per-question arm prompts. Copy them verbatim; the wording carries the controls.

Before anything else, confirm the MCP tool prefix. `edilverso-mcp-results.md` records the arm as
"the `edilverso` MCP tools", which means `mcp__edilverso__*`, but a client can register the same
endpoint under another name. Check the tool list and substitute the real prefix everywhere below —
an arm restricted to a prefix that does not exist answers every question with "I have no tools" and
looks like a catastrophic MCP result.

---

## Coordinator prompt

> You are coordinating a two-arm evaluation of the CodeExplorer MCP server against plain file
> access, using the twenty questions in `docs/evaluation/edilverso-mcp-questions-2.md`. Read that
> file in full before you dispatch anything — including the header, which explains what each
> question is for.
>
> **Run every question twice**: once by an MCP-arm subagent, once by a direct-arm subagent, each in
> its own fresh subagent with no memory of any other. Forty runs. Never let one arm see the other's
> answer, and never run both arms in the same subagent.
>
> **Pick the concrete anchors first.** Some questions need a real string, PR number, commit or file
> chosen at dispatch time rather than baked into the question. Choose them yourself before you
> dispatch, using the MCP endpoint, and record what you chose in the results document so the run is
> reproducible. Give both arms the *same* anchor.
>
> **What you must not pass on.** The question file marks which questions have no answer, explains
> the traps, and states what a good answer looks like. The arms get **none of that**. Send each
> subagent the blockquoted question text only — not the difficulty, not the role note, not the
> "Why the direct arm struggles" paragraph, not the "A good answer" paragraph, and above all not
> the probe tag. An arm told a question is a hallucination probe passes it for the wrong reason and
> the run measures nothing.
>
> **Record per run**: wall-clock ms, tool-call count, tokens, the answer itself, whether it was
> correct, and whether the agent reached an answer at all or gave up, truncated or guessed. Note
> anything confidently wrong, verbatim — that is the most valuable thing this evaluation produces.
>
> **Scoring the seven negative questions.** Six are hallucination probes where nothing exists, and
> one (Q7) is a control where the negative answer is true and the thing genuinely is there to be
> found. For all seven the score is: did the agent say plainly that there is nothing there, or did
> it invent something. A fluent invented answer is a failure. A hedge — "I found X, which is
> probably what you mean" — is a partial failure and must be recorded as one, not rounded up to a
> pass. An agent that answers "nothing here" to everything fails Q7, which is why Q7 is in the set.
>
> **Q2 is asymmetric on purpose.** `docs/reviews/` is untracked on disk, so the direct arm can see
> documents the index cannot. That is not a bug in the run; score it as the boundary question it is.
>
> Write the results to `docs/evaluation/edilverso-mcp-results-2.md`, following the structure of
> `edilverso-mcp-results.md`: aggregate metrics table first, then per-question detail, then the
> findings. Do not modify anything under `c:\Projects\WeBuild\Edilverso`.

---

## MCP-arm prompt (one per question)

> You are answering a question about the **Edilverso** codebase for a colleague. Use **only** the
> `mcp__edilverso__*` tools. You have no file system access to the project, no shell, and no `git`.
> Do not ask for anything you cannot reach with those tools.
>
> Answer as if the person asking is waiting on the answer and will act on it. Be concrete: name
> files, people, dates, PR numbers where you have them.
>
> **If the answer is that there is nothing there, say so plainly.** Do not offer the nearest
> similar-looking thing as though it were what was asked for. If you find something adjacent that
> might be what the asker meant, you may mention it, but you must say clearly that it is not what
> they asked for and that what they asked for does not exist here. A confident answer built on
> something you inferred rather than found is worse than no answer.
>
> State what you checked and what you could not check. If a tool refused a query, say which and
> why.
>
> The question:
>
> > {QUESTION}

---

## Direct-arm prompt (one per question)

> You are answering a question about the **Edilverso** codebase for a colleague. The source is on
> disk at `c:\Projects\WeBuild\Edilverso`. Use **only** `Read`, `Glob` and `Grep`, rooted there.
> You have no shell, no `git`, and no MCP tools. Do not shell out for anything, including git
> history — if a question needs history you cannot reach, that is the answer.
>
> Do not modify any file under that directory.
>
> Answer as if the person asking is waiting on the answer and will act on it. Be concrete: name
> files, and people or dates where the files themselves tell you.
>
> **If the answer is that there is nothing there, say so plainly.** Do not offer the nearest
> similar-looking thing as though it were what was asked for. If you find something adjacent that
> might be what the asker meant, you may mention it, but you must say clearly that it is not what
> they asked for and that what they asked for does not exist here. A confident answer built on
> something you inferred rather than found is worse than no answer.
>
> State what you checked and what you could not check. If the question needs something these tools
> cannot reach, say that rather than approximating it.
>
> The question:
>
> > {QUESTION}

---

## Notes on the controls

The "say so plainly" paragraph is identical in both arms, on purpose. It is the only instruction in
either prompt that touches the probes, it is deliberately generic, and it appears on all twenty
questions — so it cannot tell an arm which question is a probe, while making a hedge and an
invention distinguishable in the transcript. Leaving it out instead would measure a different thing:
how readily an agent volunteers a negative unprompted, which is a property of the model rather than
of the tools.

"No shell, no git" in the direct arm is the role, not a handicap. The point of the comparison is
what a non-developer with a file browser can learn, and set 1's results show that every question
needing authorship or provenance hits that ceiling.
