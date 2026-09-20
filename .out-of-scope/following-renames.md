# Following Renames

A path-scoped history answer counts the commits recorded under **that path**, and no others. It does
not merge in the commits of whatever the path was called before a rename. Renames are **signalled,
never followed**.

This is a decision, not a gap. The signal exists and is thorough: since #131, a scope whose history a
rename split names the earlier path, gives the combined commit total across the whole chain, and
spells out the call that reads the earlier path.

## Why this is out of scope

**The number an agent wants is already in the answer.** `PathLineage.CombinedCommits` is the
distinct-commit total across the scope and its whole chain, de-duplicated across the rename commit
that touches both sides, and it is printed on every scoped reply that has a previous path. Following
renames would not tell an agent anything about *how much* history exists that it is not already told.
What following would add is a merged listing or ranking — and that is one call away, with the path
the reply spells out.

**The contract it would invert is stated in ten places that currently agree.** Seven of them are the
tool surface an agent reads before calling:

- `git_log` — the `path` bullet and the `path` parameter description
- `authors` — the `path` bullet and the `path` parameter description
- `file_history` — the history bullet
- `hot_files` — the `directory` bullet and the `directory` parameter description
- `co_changed` — the history bullet

Three more are reply prose: the shared `ByRecordedPath` note at the foot of every scoped answer,
`WhyNoCommits`, and the "moves alone" branch of `co_changed`. `CONTEXT.md` states it twice — under
**History** ("a file's history begins where it was last renamed") and under **Previous Path**
("Renames are **signalled, never followed**") — and two tests assert the wording directly.

Inverting that has to happen everywhere at once, or the tools stop agreeing about what a scope is.
That disagreement is not hypothetical: it is precisely the fault #132 and #136 each existed to
correct, where one tool answered for a path its neighbour refused as a typo. Re-creating it to save a
round trip is a bad trade.

**An opt-in `follow` argument does not rescue it** — rejected in #131 and the reasoning holds: a flag
an agent must know to pass only helps the agent that already suspects a rename, which is not the
agent that needs help.

## What would reopen this

A case where the second call is genuinely not enough — an agent that needs one ranking or one log
over the chain, not two. Nobody has produced one. The evidence that motivated #131 pointed the other
way: the failure was *silence*, an agent told 9 commits for a directory with 1,252 and given no
reason to look further, and silence is what the signal fixed.

Note that this does not cover `co_changed`. Coupling is the one read the previous-path signal can
name a gap in and point nowhere for, because `git_log` and `file_history` do not answer it — that is
its own question, tracked separately, and it is not this rejection.

## Prior requests

- #142: "Should a path-scoped count follow renames, or keep signalling them?"
