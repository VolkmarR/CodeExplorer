# Indexing UTF-16 Text Files

A file saved as UTF-16 with a byte order mark is **binary** to this index, and it stays that way.
It is skipped at the reading step with the reason "binary", so `grep`, `find_references` and
`find_definition` never see its lines, and `read_file` says why rather than returning nothing.

This is a decision, not a gap. The decoder can read UTF-16 (`BlobText.Decode` honours both marks,
and a test proves it). What stops these files is the binary verdict that comes before decoding,
and that verdict is libgit2's on purpose.

## Why this is out of scope

**One classifier for the whole index.** Whether a blob is binary is libgit2's call (`git_blob_is_binary`),
the same call git makes: a NUL in the first bytes. UTF-16 text written in ASCII is half NULs, so
libgit2 calls it binary, and so does its diff. The history pass records those files' changes the
same way: no line counts, no edits, no attribution. Indexing them as text at HEAD would give files
whose lines are searchable and readable while every history read says they have no lines, and
`blame` would have nothing for any of them. The tools would disagree about the same file, and that
disagreement is what the index is built to avoid.

**Carving out an exception means maintaining our own heuristic.** Accepting a BOM ahead of
libgit2's verdict is the first rule of a classifier of our own. #263 decided the opposite for the
same code path: keep libgit2's verdict exactly, and fix only the double inflation around it. A
second rule set changes which files are skipped, and every later encoding question then has two
answers to keep in step.

**Making history agree costs a decoded diff.** Treating these files as text consistently would mean
decoding both sides of every UTF-16 change and diffing the text ourselves, instead of reading the
patch libgit2 already renders. That is a second diff engine for a small share of files.

**The skip is visible.** An agent that opens such a file is told it was skipped as binary. A file
that is silently missing would be a different problem.

## What would reopen this

A project this server serves where UTF-16 files hold code agents need to search, such as SQL
scripts or resource files that are the only place a name is written, and where re-saving them as
UTF-8 in the repository is not an option. The reopening would then have to cover the history pass
too, not only the file pass.

## Prior requests

- #264: "Files with a UTF-16 byte order mark are treated as binary and never indexed"
