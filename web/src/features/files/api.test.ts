import { expect, test } from 'vite-plus/test'
import {
  fetchBlame,
  fetchBrowse,
  fetchDeclarations,
  fetchFile,
  fetchImports,
  fetchTree,
} from '@/features/files/api'
import { recordRequest } from '@/testing/recordRequest'

/**
 * The requests the browse and file views make, pinned byte for byte. A browse listing never sends
 * the tree's `path`: the glob answers for the whole project.
 */
test('a listing sends its glob and page always and its repository only when set', async () => {
  expect(await recordRequest(() => fetchBrowse('demo', { glob: '', page: 1, path: '' }))).toBe(
    '/api/projects/demo/files?glob=&page=1',
  )
  const narrowed = { glob: '*.cs', page: 2, path: 'main/src', repository: 'main' }
  expect(await recordRequest(() => fetchBrowse('demo', narrowed))).toBe(
    '/api/projects/demo/files?glob=*.cs&page=2&repository=main',
  )
})

test('a level of the tree and each read of one file send the path alone', async () => {
  const path = 'main/src a/Program.cs'
  const encoded = 'path=main%2Fsrc+a%2FProgram.cs'
  expect(await recordRequest(() => fetchTree('demo', ''))).toBe('/api/projects/demo/tree?path=')
  expect(await recordRequest(() => fetchTree('demo', 'main/src a'))).toBe(
    '/api/projects/demo/tree?path=main%2Fsrc+a',
  )
  expect(await recordRequest(() => fetchFile('demo', path))).toBe(
    `/api/projects/demo/file?${encoded}`,
  )
  expect(await recordRequest(() => fetchBlame('demo', path))).toBe(
    `/api/projects/demo/file/blame?${encoded}`,
  )
  expect(await recordRequest(() => fetchImports('demo', path))).toBe(
    `/api/projects/demo/file/imports?${encoded}`,
  )
  expect(await recordRequest(() => fetchDeclarations('demo', path))).toBe(
    `/api/projects/demo/file/declarations?${encoded}`,
  )
})
