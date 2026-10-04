# Reserving Disk Room for the Full-Text Index

Before a settle or a refresh starts, it checks for free disk space with one estimate:
`max(floor, 2 × live file size)`. That estimate does **not** add room for the BM25 index the step
will build, even when the live file was restored without one. This is a decision, not a gap.

## Why this is out of scope

**The path is rare.** A settle runs only after a refresh that began with a restore ends without its
swap (ADR-0009). The ordinary refresh after a restore is checked with the same estimate and swaps
normally.

**A settle that runs out of space recovers.** Since #349, a settle that fails deletes its partial file,
and the project stays marked for the next settle. The disk check (#363) then refuses the next attempt
once free space is near the floor. So when the estimate is too low, the cost is one failed attempt and
a project that keeps using substring scan a little longer. It is not a corrupt index.

**A better estimate costs more than it saves.** The options considered were:

- **A.** Estimate the BM25 size from the `lines` table's text, or from the full-text size the last
  full build recorded. The recorded figure is often missing on a replica that has just woken up,
  which is exactly where a settle runs. The estimate would also need its own tests and its own
  paragraph in ADR-0009.
- **B.** Use a larger multiplier for the settle only, such as 3×. That is a guess, and it would refuse
  settles that would have fit.

Both refuse more settles on a tight disk, and each refused settle extends the substring-scan gap,
which is the very thing the settle exists to close.

## What would reopen it

A measured case where a settle or a refresh after a restore filled the disk and something else on that
disk failed because of it, such as another project's refresh or a live index's write. The disk briefly
filling up is the real risk accepted here. Measuring the full-text index's share of an index file is
the place to start.

## Prior requests

- #371: "Decide: should the full-text settle reserve disk room for the index it builds?"
