import { expect, test } from 'vite-plus/test'
import { validateBrowseSearch } from '@/lib/urls/browseParams'
import {
  CHURN_DEFAULTS,
  CHURN_WINDOWS,
  churnSearch,
  DEFAULT_CHURN_DAYS,
  describeWindow,
  MAX_CHURN_DEPTH,
  validateChurnSearch,
} from '@/lib/urls/churnParams'
import { validateCommitSearch } from '@/lib/urls/commitParams'
import { validateFileSearch } from '@/lib/urls/fileParams'
import { validateHistorySearch } from '@/lib/urls/historyParams'
import { validateSearch } from '@/lib/urls/searchParams'

/**
 * Every `validateSearch` is hand-written coercion standing between a pasted or hand-edited link and
 * a page that must still open. The router types the result, so nothing here fails to compile when a
 * branch is wrong — it just opens the wrong view quietly. Each module gets the same two questions:
 * the coercion table, and one deliberately broken URL that has to land on the documented defaults.
 */

/** What the router hands a validator: the query string, parsed, with every value still a string. */
function fromUrl(query: string): Record<string, unknown> {
  return Object.fromEntries(new URLSearchParams(query))
}

test('search: the coercion table', () => {
  const cases: [Record<string, unknown>, ReturnType<typeof validateSearch>][] = [
    // A boolean arrives as the string `true` from a URL and as a real boolean from a typed link.
    [{ regex: 'true' }, { caseSensitive: false, page: 1, q: '', regex: true }],
    [{ regex: true }, { caseSensitive: false, page: 1, q: '', regex: true }],
    [{ regex: 'false' }, { caseSensitive: false, page: 1, q: '', regex: false }],
    [{ regex: 1 }, { caseSensitive: false, page: 1, q: '', regex: false }],
    [{ caseSensitive: 'true' }, { caseSensitive: true, page: 1, q: '', regex: false }],
    // Page is a positive integer or the first page; anything else is a typo, not a page.
    [{ page: '3' }, { caseSensitive: false, page: 3, q: '', regex: false }],
    [{ page: '0' }, { caseSensitive: false, page: 1, q: '', regex: false }],
    [{ page: '-2' }, { caseSensitive: false, page: 1, q: '', regex: false }],
    [{ page: '2.5' }, { caseSensitive: false, page: 1, q: '', regex: false }],
    [{ page: 'last' }, { caseSensitive: false, page: 1, q: '', regex: false }],
    [{ page: '' }, { caseSensitive: false, page: 1, q: '', regex: false }],
    // An empty filter is no filter: `extension=` must not narrow the search to files with none.
    [{ extension: '' }, { caseSensitive: false, page: 1, q: '', regex: false }],
    [{ extension: 'cs' }, { caseSensitive: false, extension: 'cs', page: 1, q: '', regex: false }],
    [{ path: '' }, { caseSensitive: false, page: 1, q: '', regex: false }],
    [{ path: 'src/' }, { caseSensitive: false, page: 1, path: 'src/', q: '', regex: false }],
    // The query itself: absent and non-string both read as the resting state, not as undefined.
    [{ q: 42 }, { caseSensitive: false, page: 1, q: '', regex: false }],
    [{}, { caseSensitive: false, page: 1, q: '', regex: false }],
  ]
  for (const [input, expected] of cases) {
    expect(validateSearch(input), JSON.stringify(input)).toEqual(expected)
  }
})

test('search: a hand-edited URL with a bad page, a string boolean and an empty filter opens the default search', () => {
  expect(validateSearch(fromUrl('q=Run&page=0&regex=true&caseSensitive=yes&extension='))).toEqual({
    caseSensitive: false,
    page: 1,
    q: 'Run',
    regex: true,
  })
})

test('file: the coercion table', () => {
  const cases: [Record<string, unknown>, ReturnType<typeof validateFileSearch>][] = [
    [{ path: 'main/src/Program.cs' }, { path: 'main/src/Program.cs' }],
    [{}, { path: '' }],
    [{ path: 12 }, { path: '' }],
    // A line is a positive integer or no line at all; line 0 does not exist in an editor's counting.
    [
      { line: '42', path: 'a' },
      { line: 42, path: 'a' },
    ],
    [{ line: '0', path: 'a' }, { path: 'a' }],
    [{ line: '-1', path: 'a' }, { path: 'a' }],
    [{ line: '4.5', path: 'a' }, { path: 'a' }],
    [{ line: '', path: 'a' }, { path: 'a' }],
    [{ line: 'top', path: 'a' }, { path: 'a' }],
    // The origin is a view name or nothing; an unknown one reads as the route's own view.
    [
      { from: 'history', path: 'a' },
      { from: 'history', path: 'a' },
    ],
    [{ from: 'blame', path: 'a' }, { path: 'a' }],
    [{ from: '', path: 'a' }, { path: 'a' }],
    [{ fromCommit: '', path: 'a' }, { path: 'a' }],
    [
      { fromCommit: 'abc123', path: 'a' },
      { fromCommit: 'abc123', path: 'a' },
    ],
  ]
  for (const [input, expected] of cases) {
    expect(validateFileSearch(input), JSON.stringify(input)).toEqual({
      from: undefined,
      fromCommit: undefined,
      line: undefined,
      ...expected,
    })
  }
})

test('file: a hand-edited URL with a bad line, an unknown origin and an empty commit opens the file plain', () => {
  expect(validateFileSearch(fromUrl('path=main/a.cs&line=0&from=blame&fromCommit='))).toEqual({
    from: undefined,
    fromCommit: undefined,
    line: undefined,
    path: 'main/a.cs',
  })
})

test('browse: the coercion table', () => {
  const cases: [Record<string, unknown>, ReturnType<typeof validateBrowseSearch>][] = [
    // The empty glob is the tree mode and is a value, not an absence: it must survive coercion.
    [{}, { glob: '', page: 1, path: '', repository: undefined }],
    [{ glob: '' }, { glob: '', page: 1, path: '', repository: undefined }],
    [{ glob: '*.cs' }, { glob: '*.cs', page: 1, path: '', repository: undefined }],
    [{ glob: true }, { glob: '', page: 1, path: '', repository: undefined }],
    [{ page: '2' }, { glob: '', page: 2, path: '', repository: undefined }],
    [{ page: '0' }, { glob: '', page: 1, path: '', repository: undefined }],
    [{ page: 'two' }, { glob: '', page: 1, path: '', repository: undefined }],
    [{ path: 'main/src' }, { glob: '', page: 1, path: 'main/src', repository: undefined }],
    // `repository=` is the select's way of saying every repository, not a repository with no name.
    [{ repository: '' }, { glob: '', page: 1, path: '', repository: undefined }],
    [{ repository: 'main' }, { glob: '', page: 1, path: '', repository: 'main' }],
  ]
  for (const [input, expected] of cases) {
    expect(validateBrowseSearch(input), JSON.stringify(input)).toEqual(expected)
  }
})

test('browse: a hand-edited URL with a bad page and an empty repository opens the tree at the root', () => {
  expect(validateBrowseSearch(fromUrl('glob=&page=-1&path=&repository='))).toEqual({
    glob: '',
    page: 1,
    path: '',
    repository: undefined,
  })
})

test('history: the coercion table', () => {
  const cases: [Record<string, unknown>, ReturnType<typeof validateHistorySearch>][] = [
    [{}, { page: 1, repository: undefined }],
    [{ page: '7' }, { page: 7, repository: undefined }],
    [{ page: 7 }, { page: 7, repository: undefined }],
    [{ page: '0' }, { page: 1, repository: undefined }],
    [{ page: '-3' }, { page: 1, repository: undefined }],
    [{ page: '1.5' }, { page: 1, repository: undefined }],
    [{ page: 'next' }, { page: 1, repository: undefined }],
    [{ page: '' }, { page: 1, repository: undefined }],
    [{ repository: '' }, { page: 1, repository: undefined }],
    [{ repository: 'acslib' }, { page: 1, repository: 'acslib' }],
    [{ repository: 0 }, { page: 1, repository: undefined }],
  ]
  for (const [input, expected] of cases) {
    expect(validateHistorySearch(input), JSON.stringify(input)).toEqual(expected)
  }
})

test('history: a hand-edited URL with a bad page and an empty repository opens page one of everything', () => {
  expect(validateHistorySearch(fromUrl('page=none&repository='))).toEqual({
    page: 1,
    repository: undefined,
  })
})

test('commit: the coercion table', () => {
  const cases: [Record<string, unknown>, ReturnType<typeof validateCommitSearch>][] = [
    [{}, { from: undefined, sha: '' }],
    [{ sha: 'a1b2c3d' }, { from: undefined, sha: 'a1b2c3d' }],
    [{ sha: 12 }, { from: undefined, sha: '' }],
    [{ sha: '' }, { from: undefined, sha: '' }],
    [
      { from: 'churn', sha: 'a' },
      { from: 'churn', sha: 'a' },
    ],
    [
      { from: 'files', sha: 'a' },
      { from: 'files', sha: 'a' },
    ],
    [
      { from: '', sha: 'a' },
      { from: undefined, sha: 'a' },
    ],
    [
      { from: 'blame', sha: 'a' },
      { from: undefined, sha: 'a' },
    ],
    [
      { from: true, sha: 'a' },
      { from: undefined, sha: 'a' },
    ],
  ]
  for (const [input, expected] of cases) {
    expect(validateCommitSearch(input), JSON.stringify(input)).toEqual(expected)
  }
})

test('commit: a hand-edited URL with an unknown origin and an empty sha opens the page naming no commit', () => {
  expect(validateCommitSearch(fromUrl('sha=&from=nowhere'))).toEqual({
    from: undefined,
    sha: '',
  })
})

test('churn: the coercion table', () => {
  const cases: [Record<string, unknown>, ReturnType<typeof validateChurnSearch>][] = [
    [{}, { days: DEFAULT_CHURN_DAYS, repository: undefined }],
    // Any positive integer, not only the offered windows: 45 days is a question, not a broken URL.
    [{ days: '45' }, { days: 45, repository: undefined }],
    [{ days: 14 }, { days: 14, repository: undefined }],
    [{ days: '0' }, { days: DEFAULT_CHURN_DAYS, repository: undefined }],
    [{ days: '-7' }, { days: DEFAULT_CHURN_DAYS, repository: undefined }],
    [{ days: '7.5' }, { days: DEFAULT_CHURN_DAYS, repository: undefined }],
    [{ days: 'year' }, { days: DEFAULT_CHURN_DAYS, repository: undefined }],
    [{ days: '' }, { days: DEFAULT_CHURN_DAYS, repository: undefined }],
    [{ repository: '' }, { days: DEFAULT_CHURN_DAYS, repository: undefined }],
    [{ repository: 'main' }, { days: DEFAULT_CHURN_DAYS, repository: 'main' }],
  ]
  for (const [input, expected] of cases) {
    expect(validateChurnSearch(input), JSON.stringify(input)).toEqual(expected)
  }
})

test('churn: a hand-edited URL with a bad window and an empty repository opens the default quarter', () => {
  expect(validateChurnSearch(fromUrl('days=all&repository='))).toEqual({
    days: DEFAULT_CHURN_DAYS,
    repository: undefined,
  })
})

test('every offered churn window survives its own URL', () => {
  for (const days of CHURN_WINDOWS) {
    expect(validateChurnSearch(fromUrl(`days=${days}`)).days).toBe(days)
  }
})

/**
 * The rollup depth, which is the one churn param where absent and zero are different rankings: the
 * server reads no depth as "rank files" and clamps a 0 up to 1, so a URL carrying one would open a
 * ranking of directories where the link meant files (#161).
 */
test('churn: a depth is a positive integer or is not there at all', () => {
  const cases: [Record<string, unknown>, number | undefined][] = [
    [{}, undefined],
    [{ depth: '1' }, 1],
    [{ depth: 3 }, 3],
    [{ depth: '0' }, undefined],
    [{ depth: '-2' }, undefined],
    [{ depth: '1.5' }, undefined],
    [{ depth: 'deep' }, undefined],
    [{ depth: '' }, undefined],
    // Clamped rather than refused: the server clamps too, and a link asking to go deeper than the
    // page offers is a question, not a broken URL.
    [{ depth: '99' }, MAX_CHURN_DEPTH],
  ]
  for (const [input, expected] of cases) {
    expect(validateChurnSearch(input).depth, JSON.stringify(input)).toBe(expected)
  }
})

test('churn: a directory and an extension list survive their own URL, and an empty one does not', () => {
  expect(validateChurnSearch(fromUrl('directory=main/src&extensions=.cs,.ts'))).toEqual({
    days: DEFAULT_CHURN_DAYS,
    depth: undefined,
    directory: 'main/src',
    extensions: '.cs,.ts',
    repository: undefined,
  })
  expect(validateChurnSearch(fromUrl('directory=&extensions='))).toEqual({
    days: DEFAULT_CHURN_DAYS,
    depth: undefined,
    directory: undefined,
    extensions: undefined,
    repository: undefined,
  })
})

/**
 * The bug the change-shaped `churnSearch` exists to prevent: every control on the page writes the
 * whole search, so one that rebuilt it from its own field would silently clear the others. A reader
 * who filters to `.cs` and then picks a longer window asked for that window OF that filter.
 */
test('churn: changing one control keeps every other', () => {
  const filtered = validateChurnSearch(fromUrl('days=14&directory=main/src&extensions=.cs&depth=2'))

  expect(churnSearch(filtered, { days: 365 })).toEqual({
    days: 365,
    depth: 2,
    directory: 'main/src',
    extensions: '.cs',
    repository: undefined,
  })
  expect(churnSearch(filtered, { extensions: '' }).extensions).toBe(undefined)
  expect(churnSearch(filtered, { depth: undefined }).depth).toBe(undefined)
})

/**
 * A directory is a qualified path and carries its own repository (ADR-0006), so the two scopes
 * cannot both be set — one of them would have to lose, and a ranking that kept a repository the
 * reader never chose is the one that reads as a bug.
 */
test('churn: a directory clears the repository, and clearing it leaves neither', () => {
  const scoped = churnSearch(CHURN_DEFAULTS, { repository: 'main' })
  expect(scoped.repository).toBe('main')

  const drilled = churnSearch(scoped, { directory: 'main/src/Api' })
  expect(drilled).toEqual({
    days: DEFAULT_CHURN_DAYS,
    depth: undefined,
    directory: 'main/src/Api',
    extensions: undefined,
    repository: undefined,
  })

  const out = churnSearch(drilled, { directory: '', repository: '' })
  expect(out.directory).toBe(undefined)
  expect(out.repository).toBe(undefined)
})

test('a window is named once, so the select and the heading cannot disagree', () => {
  expect(describeWindow(14)).toBe('Last 2 weeks')
  expect(describeWindow(90)).toBe('Last 3 months')
  expect(describeWindow(365)).toBe('Last year')
  expect(describeWindow(3650)).toBe('All history')
  // A year is named before the month rule can call it twelve months, and anything longer than the
  // longest offered window is all of it.
  expect(describeWindow(7300)).toBe('All history')
  expect(describeWindow(30)).toBe('Last 1 months')
  expect(describeWindow(7)).toBe('Last 1 weeks')
  // A hand-written window that is neither: named in the unit it was asked in.
  expect(describeWindow(45)).toBe('Last 45 days')
  expect(describeWindow(1)).toBe('Last 1 days')
})
