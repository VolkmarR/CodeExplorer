import { expect, test } from 'vite-plus/test'
import { fetchChurn } from '@/features/churn/api'
import { recordRequest } from '@/lib/recordRequest'

/** The ranking's request, pinned byte for byte: every unset scope and filter is left off. */
test('a churn ranking sends its window always and the rest only when set', async () => {
  expect(await recordRequest(() => fetchChurn('demo', { days: 90 }))).toBe(
    '/api/projects/demo/churn?days=90',
  )
  expect(await recordRequest(() => fetchChurn('demo', { days: 14, repository: 'main' }))).toBe(
    '/api/projects/demo/churn?days=14&repository=main',
  )
  expect(
    await recordRequest(() =>
      fetchChurn('demo', {
        days: 365,
        depth: 2,
        directory: 'main/src/Api',
        extensions: '.cs,.ts',
        repository: 'main',
      }),
    ),
  ).toBe(
    '/api/projects/demo/churn?days=365&directory=main%2Fsrc%2FApi&depth=2&extensions=.cs%2C.ts&repository=main',
  )
})
