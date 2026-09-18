import { expect, test } from 'vite-plus/test'
import { activeView, pageTrail } from '@/components/appNavigation'

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
  expect(activeView('/projects/new')).toBe(null)
  expect(activeView('/')).toBe(null)
})

test('a commit lights History, which is the view its route belongs under', () => {
  expect(activeView('/projects/acslib/commit')).toBe('history')
})

/**
 * The trail is the other half: which item is lit is a fact about the route, and which trail is drawn
 * is a fact about the link that was followed. Only the two pages reachable from more than one view
 * can tell them apart, and those are exactly the cases worth pinning.
 */

test('a view names itself and nothing below it', () => {
  expect(pageTrail('/projects/acslib/churn', {})).toEqual([{ kind: 'view', view: 'churn' }])
})

test('a file opened from Files reads as Files, and the same file from a commit reads as History', () => {
  expect(pageTrail('/projects/acslib/file', { path: 'acslib/src/Widget.cs' })).toEqual([
    { kind: 'view', view: 'files' },
    { kind: 'file', path: 'acslib/src/Widget.cs' },
  ])

  expect(
    pageTrail('/projects/acslib/file', {
      from: 'history',
      fromCommit: 'abc1234def',
      path: 'acslib/src/Widget.cs',
    }),
  ).toEqual([
    { kind: 'view', view: 'history' },
    { kind: 'commit', sha: 'abc1234def' },
    { kind: 'file', path: 'acslib/src/Widget.cs' },
  ])
})

test('a file opened from a view with no commit names the view alone above it', () => {
  // A search result is not a commit, and the trail must not invent one to sit between them.
  expect(
    pageTrail('/projects/acslib/file', { from: 'search', path: 'acslib/src/Widget.cs' }),
  ).toEqual([
    { kind: 'view', view: 'search' },
    { kind: 'file', path: 'acslib/src/Widget.cs' },
  ])
})

test('a commit sits under History unless the link said otherwise', () => {
  expect(pageTrail('/projects/acslib/commit', { sha: 'abc1234def' })).toEqual([
    { kind: 'view', view: 'history' },
    { kind: 'commit', sha: 'abc1234def' },
  ])

  expect(pageTrail('/projects/acslib/commit', { from: 'files', sha: 'abc1234def' })).toEqual([
    { kind: 'view', view: 'files' },
    { kind: 'commit', sha: 'abc1234def' },
  ])
})

test('a hand-edited origin is ignored rather than trusted, and a pasted URL still opens', () => {
  // `from` comes out of the address bar, so it names anything at all; an unknown view falls back to
  // the default rather than putting a crumb in the trail that no sidebar item answers to.
  expect(pageTrail('/projects/acslib/commit', { from: 'nonsense', sha: 'abc1234def' })).toEqual([
    { kind: 'view', view: 'history' },
    { kind: 'commit', sha: 'abc1234def' },
  ])

  // No subject at all: the route answers not-found, and the trail stops at the view rather than
  // naming an empty commit or an empty file.
  expect(pageTrail('/projects/acslib/commit', {})).toEqual([{ kind: 'view', view: 'history' }])
  expect(pageTrail('/projects/acslib/file', {})).toEqual([{ kind: 'view', view: 'files' }])
})

test('a page on no view has no trail below the project', () => {
  expect(pageTrail('/projects/new', {})).toEqual([])
  expect(pageTrail('/', {})).toEqual([])
})
