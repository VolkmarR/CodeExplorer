import { expect, test } from 'vite-plus/test'
import { fetchCommit, fetchCommitFiles, fetchCommits } from '@/features/history/api'
import { recordRequest } from '@/lib/recordRequest'

/** The change log's requests, pinned byte for byte. */
test('a page of the log sends its page always and its repository only when set', async () => {
  expect(await recordRequest(() => fetchCommits('demo', 1))).toBe(
    '/api/projects/demo/commits?page=1',
  )
  expect(await recordRequest(() => fetchCommits('demo', 4, 'main'))).toBe(
    '/api/projects/demo/commits?page=4&repository=main',
  )
})

test('a commit and its files are read by path alone', async () => {
  expect(await recordRequest(() => fetchCommit('demo', 'a1b2c3d'))).toBe(
    '/api/projects/demo/commits/a1b2c3d',
  )
  expect(await recordRequest(() => fetchCommitFiles('demo', 'a1b2c3d'))).toBe(
    '/api/projects/demo/commits/a1b2c3d/files',
  )
})
