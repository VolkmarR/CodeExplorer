import { expect, test } from 'vite-plus/test'
import { newestImportedAt } from '@/features/history/historyWindow'

const commit = (authoredAt: string) => ({
  authorName: 'V',
  authoredAt,
  sha: authoredAt,
  subject: 's',
})

/**
 * The end of every window in this app is the newest commit the index holds, never the clock
 * (CONTEXT.md, _Window_). Which commit that is comes from the repositories, and the cases worth
 * asserting are the ones where they disagree or where one of them has no history at all.
 */

test('the newest of the repositories is the newest the project holds', () => {
  expect(
    newestImportedAt([
      { newestCommit: commit('2026-08-01T00:00:00Z') },
      { newestCommit: commit('2026-09-16T00:00:00Z') },
      { newestCommit: commit('2026-05-04T00:00:00Z') },
    ]),
  ).toBe('2026-09-16T00:00:00Z')
})

test('a repository without history is skipped rather than counted as old', () => {
  expect(
    newestImportedAt([{ newestCommit: null }, { newestCommit: commit('2026-09-16T00:00:00Z') }]),
  ).toBe('2026-09-16T00:00:00Z')
})

test('a project with no imported history anywhere has no window to state', () => {
  expect(newestImportedAt([])).toBe(null)
  expect(newestImportedAt([{ newestCommit: null }])).toBe(null)
})
