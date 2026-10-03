import { expect, test } from 'vite-plus/test'
import { fetchSearch } from '@/features/search/api'
import { recordRequest } from '@/testing/recordRequest'

/**
 * The request a search makes, pinned byte for byte: the booleans travel as words even when false,
 * and an absent filter is left off rather than sent empty.
 */
test('a search sends its four fields always and its filters only when set', async () => {
  const bare = { caseSensitive: false, page: 1, q: 'Run(', regex: false }
  expect(await recordRequest(() => fetchSearch('demo', bare))).toBe(
    '/api/projects/demo/search?caseSensitive=false&page=1&q=Run%28&regex=false',
  )
  expect(
    await recordRequest(() =>
      fetchSearch('demo', {
        caseSensitive: true,
        extension: 'cs',
        page: 3,
        path: 'main/src a/',
        q: 'a b&c',
        regex: true,
      }),
    ),
  ).toBe(
    '/api/projects/demo/search?caseSensitive=true&page=3&q=a+b%26c&regex=true&extension=cs&path=main%2Fsrc+a%2F',
  )
})
