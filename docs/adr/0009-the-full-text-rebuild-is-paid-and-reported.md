# The full-text index is rebuilt whole on every refresh, and the refresh says what that cost

`ShadowIndex.CompleteAsync` runs `create_fts_index` over the whole `lines` table every time a
project is refreshed, including a refresh that found nothing new. That stays. What changes is that
the cost is no longer invisible: a refresh records what each of its phases spent and keeps it on the
status, so the figure can be read off any refresh instead of being re-derived by measurement.

On the real Radix index — 9,495,301 lines, 49,689 files — the rebuild measured 27.9 s, two to three
times the attribution work and the largest single item between the end of the history import and the
durable store. On a smaller project with nothing new to ingest it was 1.17 s of a 2.67 s refresh:
44%, and again the largest item. It does not shrink when the ingest has nothing to do, and the
reason is structural rather than incidental.

## Why it cannot simply be made incremental

The shadow is a fresh file on every refresh. `CarryHistoryAsync` brings `commits`, `commit_files`
and `attribution` across; `files` and `lines` are re-ingested from the local copies. So there is no
existing full-text index in the shadow to add to, and DuckDB's FTS is a built index over a table
rather than something maintained per row. "Only index what changed" is therefore not a smaller
version of what happens today — it is one of three different designs, and each was rejected.

**Carrying the full-text index across the swap** fails for the reason carrying `lines.commit_id`
failed in #80: it is derived from content that changes, and a stale full-text index answers a search
with lines that are no longer in the file. A search that returns deleted code as though it were
present is the one failure mode this server cannot have, because an agent cannot tell it from a
right answer.

**Rebuilding only when the ingest changed something** cannot stand on its own, because skipping the
rebuild means having the previous index to skip to — which means carrying `lines` and the index
across the swap, which is the option above with its objection. It would also help only the no-op
refresh and leave the ordinary one paying in full.

**Building it after the swap, or on first use**, is the one that would actually take the cost out of
a refresh, and the pieces for it exist: `index_info.full_text` already records whether a project has
a BM25 index, and the substring scan is a supported, documented fallback (ADR-0004), so a project
could serve searches by scan while its index is built and flip the flag when it is ready. It is
rejected here and not forever, because it builds an index on a database that is already answering
queries, and `CODING_STANDARDS.md` is unconditional that a refresh builds a shadow and swaps and
never mutates a live index in place. Reversing that is a decision about the swap invariant, not a
line in a ticket about full-text search, and it would arrive as an amendment to this ADR.

## What is paid for what

A refresh is not on the path of any agent's request. It runs on a schedule and on operator action,
the old index keeps answering searches throughout, and the swap is the only moment anything changes
for a caller. So the thing being spent is a background window, and what it buys is that every search
between this refresh and the next is BM25 over a complete index rather than a substring scan — which
ranks differently, and which ADR-0004 keeps only as the offline fallback.

The cost is therefore real but not urgent, and the honest response to a real-but-not-urgent cost is
to make it legible rather than to hide it or to trade an invariant for it.

## Consequences

- **A refresh's status carries what each phase cost, and keeps it after the refresh ends.** The
  status held one phase at a time, so a refresh that had finished reported `Done` and nothing else,
  and attributing one took a poller fast enough to catch phases lasting milliseconds. #80 was opened
  against a measurement made that way and blamed the wrong statement; a day of triage went into
  re-deriving by measurement what the status could have said. `RefreshStatus.Phases` is what stops
  that recurring, and it is the reason this ADR can quote figures at all.
- **The timeline is built from the reports a refresh already makes**, by `PhaseTimeline`, and not
  from new instrumentation. A `Telemetry` span per statement was considered and rejected in #91 as a
  hot-path cost to answer a question the status can answer; this adds no span, no builder changed,
  and the snapshot is rebuilt only on the reports that change the phase rather than on the ones a
  counting step makes every 200 items.
- **A failed refresh keeps the phases it got through.** Which phase a refresh died in is most of
  what an operator wants from one that failed, and the timeline is already there to be kept.
- **Every phase string is a constant**, including `Starting`, which was a literal while the only way
  to see it was to poll inside the milliseconds it lasts. A phase is durable data now, so a reworded
  sentence must not silently change what a test waits for or what a stored figure is labelled.
- **The progress reporting is load-bearing and not decoration.** Before #91 everything after the
  attribution ran under the attribution's label, which is what made #80 mis-scoped. A step that
  gains a separable piece of work gains a phase for it, or the next person measuring a slow refresh
  starts where #80 started.
- **The three rejected designs stay on the record with their objections.** If the 27.9 s is later
  judged not worth paying, the argument starts from a number read off a refresh rather than from one
  inferred from a status message — and deferring the build past the swap starts by amending this ADR
  and the swap invariant it leans on, which is the part that would otherwise be discovered late.
- **A refresh pays for the build once, even when it begins with a restore (#290).** On a disk
  without the project's file, a refresh restores the durable copy before it fetches, and that restore
  used to build the BM25 index the shadow was about to build again. It skips the build now. Between
  the restore and the swap the project is served by substring scan, which `index_info` records. That
  is a window of minutes on a replica that has just woken up, not a live index changed in place. A
  refresh that fails before its swap replaces the restored index with a second restore that does build
  the full-text index, so the gap never outlasts the refresh. The restore also reports a phase of its
  own, so what it cost is on the timeline, and a failed restore is reported under its own name rather
  than under `Starting`.
