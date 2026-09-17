import type { CommitRef } from '@/lib/api'

/**
 * When the history this project holds ends.
 *
 * Every window in this system is counted from the newest commit the index holds and never from the
 * clock (CONTEXT.md, _Window_), which is exactly the thing a reader cannot see and will otherwise
 * assume the other way round: a change log that stops two weeks ago looks like a quiet fortnight
 * rather than like an index nobody has refreshed.
 *
 * ISO strings compare as strings, and the server sends them in UTC, so there is no date arithmetic
 * here to get wrong.
 */
export function newestImportedAt(
  repositories: readonly { newestCommit: CommitRef | null }[],
): string | null {
  let newest: string | null = null
  for (const repository of repositories) {
    const at = repository.newestCommit?.authoredAt
    // A repository whose history never arrived says nothing about when the project's does.
    if (at && (newest === null || at > newest)) newest = at
  }
  return newest
}
