import { expect, test } from 'vite-plus/test'
import { activeView } from '@/components/appNavigation'

/**
 * The sidebar is the only navigation there is, so an item that fails to light leaves the reader
 * standing nowhere. This is the one part of the frame that grows with every view added, and the
 * cases it gets wrong are the ones where a view's route is not under its own name.
 */

test('each view is lit from its own path', () => {
  expect(activeView('/projects/acslib/search')).toBe('search')
  expect(activeView('/projects/acslib/history')).toBe('history')
  expect(activeView('/projects/acslib/churn')).toBe('churn')
  expect(activeView('/projects/acslib/files')).toBe('files')
  expect(activeView('/projects/acslib/settings')).toBe('settings')
})

test('reading a file lights Files, which is the view it was reached from', () => {
  // `/file` is its own route and not under `/files`: a link's own active state cannot know that.
  expect(activeView('/projects/acslib/file')).toBe('files')
})

test('the project itself is the overview, with or without the trailing slash', () => {
  expect(activeView('/projects/acslib')).toBe('overview')
  expect(activeView('/projects/acslib/')).toBe('overview')
})

test('a page outside a project lights nothing, rather than the nearest item', () => {
  expect(activeView('/')).toBe(null)
  expect(activeView('/projects/new')).toBe(null)
})
