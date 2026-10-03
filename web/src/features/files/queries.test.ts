import { expect, test } from 'vite-plus/test'
import {
  blameQuery,
  canBlame,
  declarationsQuery,
  fileQuery,
  importsQuery,
} from '@/features/files/queries'

/**
 * The per-file reads share one factory, and their keys are what a build's invalidation and the rail
 * and gutter's shared blame entry hang off, so they are pinned as written before the factory.
 */
test('each read of one file is keyed under the project by what it reads and the path', () => {
  const path = 'main/src/Program.cs'
  expect(fileQuery('demo', path).queryKey).toEqual(['project', 'demo', 'file', path])
  expect(importsQuery('demo', path).queryKey).toEqual(['project', 'demo', 'imports', path])
  expect(declarationsQuery('demo', path).queryKey).toEqual([
    'project',
    'demo',
    'declarations',
    path,
  ])
  const file = { lastCommit: null, qualifiedPath: path, skipReason: null }
  expect(blameQuery('demo', file).queryKey).toEqual(['project', 'demo', 'blame', path])
})

test('blame is asked for only where there is history and lines, and only when wanted', () => {
  const commit = { authorName: 'a', authoredAt: '2026-01-01', sha: 'abc', subject: 's' }
  const file = { lastCommit: commit, qualifiedPath: 'main/a.cs', skipReason: null }
  expect(canBlame(file)).toBe(true)
  expect(blameQuery('demo', file).enabled).toBe(true)
  expect(blameQuery('demo', file, false).enabled).toBe(false)
  expect(canBlame({ ...file, lastCommit: null })).toBe(false)
  expect(canBlame({ ...file, skipReason: 'binary' })).toBe(false)
})
