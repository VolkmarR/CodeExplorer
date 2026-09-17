import { expect, test } from 'vite-plus/test'
import { activeView } from '@/components/appNavigation'

/**
 * The sidebar is the only navigation there is, so an item that fails to light leaves the reader
 * standing nowhere. This is the one part of the frame that grows with every view added, and the
 * cases it gets wrong are the ones where a view's route is not under its own name.
 */

test('each view is lit from its own path', () => {
  expect(activeView('/projects/acslib/search', 'overview')).toBe('search')
  expect(activeView('/projects/acslib/history', 'overview')).toBe('history')
  expect(activeView('/projects/acslib/churn', 'overview')).toBe('churn')
  expect(activeView('/projects/acslib/files', 'overview')).toBe('files')
})

test('reading a file lights Files, which is the view it was reached from', () => {
  // `/file` is its own route and not under `/files`: a link's own active state cannot know that.
  expect(activeView('/projects/acslib/file', 'overview')).toBe('files')
})

test('the project page is two items, and the tab in the URL says which', () => {
  expect(activeView('/projects/acslib', 'overview')).toBe('overview')
  expect(activeView('/projects/acslib', 'settings')).toBe('settings')
  expect(activeView('/projects/acslib/', 'settings')).toBe('settings')
})

test('a page outside a project lights nothing, rather than the nearest item', () => {
  expect(activeView('/', 'overview')).toBe(null)
  expect(activeView('/projects/new', 'overview')).toBe(null)
})
