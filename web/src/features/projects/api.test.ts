import { expect, test } from 'vite-plus/test'
import {
  fetchExcludedPathSuggestions,
  fetchExcludedPaths,
  fetchProject,
  fetchProjectOverview,
  fetchProjects,
} from '@/features/projects/api'
import { recordRequest } from '@/lib/recordRequest'

/**
 * The overview's request, pinned byte for byte. The default view is the bare URL, the same one the
 * sidebar links to, so it is one request and one cache entry with it.
 */
test('an overview sends only the fields its view sets', async () => {
  expect(await recordRequest(() => fetchProjectOverview('demo', {}))).toBe(
    '/api/projects/demo/overview',
  )
  expect(
    await recordRequest(() =>
      fetchProjectOverview('demo', { days: 30, repository: 'two', showExcluded: true }),
    ),
  ).toBe('/api/projects/demo/overview?days=30&showExcluded=true&repository=two')
})

test('the project reads with no parameters ask by path alone', async () => {
  expect(await recordRequest(() => fetchProjects())).toBe('/api/projects')
  expect(await recordRequest(() => fetchProject('demo'))).toBe('/api/projects/demo')
  expect(await recordRequest(() => fetchExcludedPaths('demo'))).toBe(
    '/api/projects/demo/excluded-paths',
  )
  expect(await recordRequest(() => fetchExcludedPathSuggestions('demo'))).toBe(
    '/api/projects/demo/excluded-paths/suggestions',
  )
})
