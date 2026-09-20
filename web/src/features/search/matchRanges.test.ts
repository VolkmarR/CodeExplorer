import { expect, test } from 'vite-plus/test'
import { matchPattern, matchRanges } from '@/features/search/matchRanges'
import type { SearchParameters } from '@/lib/urls/searchParams'

/**
 * The marker re-derives on the client what the server already matched, so it can disagree with the
 * server silently: a wrong pattern marks the wrong span, or nothing, and the line still renders. The
 * cases below are the ones where the two engines and the two modes could drift apart.
 */
function params(overrides: Partial<SearchParameters>): SearchParameters {
  return { caseSensitive: false, page: 1, q: '', regex: false, ...overrides }
}

function rangesFor(text: string, overrides: Partial<SearchParameters>) {
  return matchRanges(text, matchPattern(params(overrides)))
}

function marked(text: string, overrides: Partial<SearchParameters>): string[] {
  return rangesFor(text, overrides).map((range) => text.slice(range.start, range.end))
}

test('a word-shaped query is marked on word boundaries only', () => {
  // The server matches whole identifiers in text mode, so marking `Run` inside `Running` would mark
  // a line the server never claimed had matched.
  expect(marked('Run the runner and Run again', { q: 'Run' })).toEqual(['Run', 'Run'])
  expect(marked('Running', { q: 'Run' })).toEqual([])
  expect(matchPattern(params({ q: 'Run' }))?.source).toBe(String.raw`\bRun\b`)
})

test('a symbol query is marked as a plain substring, because \\b beside a symbol matches nowhere', () => {
  expect(marked('a -> b -> c', { q: '->' })).toEqual(['->', '->'])
  expect(marked('task.Result', { q: '.Result' })).toEqual(['.Result'])
  expect(matchPattern(params({ q: '->' }))?.source).toBe('->')
})

test('a single word character is still word-shaped', () => {
  // `^\w$` is its own alternative in the test: one letter has no second end to anchor.
  expect(marked('x + xs', { q: 'x' })).toEqual(['x'])
})

test('text mode escapes regex metacharacters rather than honouring them', () => {
  // `.Result` must not match `xResult`, and `a+b` must be the three characters.
  expect(marked('xResult and .Result', { q: '.Result' })).toEqual(['.Result'])
  expect(marked('a+b and aab', { q: 'a+b' })).toEqual(['a+b'])
  expect(marked('cost (net)', { q: '(net)' })).toEqual(['(net)'])
  expect(marked('items[0]', { q: '[0]' })).toEqual(['[0]'])
})

test('the case flag decides whether Run and run are the same query', () => {
  expect(marked('Run run RUN', { caseSensitive: false, q: 'run' })).toEqual(['Run', 'run', 'RUN'])
  expect(marked('Run run RUN', { caseSensitive: true, q: 'run' })).toEqual(['run'])
  expect(matchPattern(params({ caseSensitive: true, q: 'run' }))?.flags).toBe('g')
  expect(matchPattern(params({ caseSensitive: false, q: 'run' }))?.flags).toBe('gi')
})

test('regex mode takes the query as written, JavaScript-only constructs included', () => {
  // RE2 has no lookaround, so the server would have rejected this; the client engine does not, and
  // the marks it draws are then its own. Nothing throws either way.
  expect(marked('Foo(); Foobar;', { q: String.raw`Foo(?=\()`, regex: true })).toEqual(['Foo'])
  expect(marked('aXa aYa', { q: '(a)X\\1', regex: true })).toEqual(['aXa'])
})

test('a pattern this engine rejects answers null instead of throwing', () => {
  expect(matchPattern(params({ q: '(unclosed', regex: true }))).toBe(null)
  expect(matchPattern(params({ q: '*', regex: true }))).toBe(null)
  // And null marks nothing, rather than every position or none of the line.
  expect(matchRanges('(unclosed', null)).toEqual([])
})

test('the empty match of `a*` marks nothing and does not loop', () => {
  expect(rangesFor('bbb', { q: 'a*', regex: true })).toEqual([])
  expect(marked('baab', { q: 'a*', regex: true })).toEqual(['aa'])
})

test('ranges come back in order and half-open', () => {
  const text = 'Run and Run'
  expect(rangesFor(text, { q: 'Run' })).toEqual([
    { end: 3, start: 0 },
    { end: 11, start: 8 },
  ])
})

test('a pattern is rewound, so two lines of one result list mark alike', () => {
  const pattern = matchPattern(params({ q: 'Run' }))
  expect(matchRanges('Run', pattern)).toEqual([{ end: 3, start: 0 }])
  expect(matchRanges('Run', pattern)).toEqual([{ end: 3, start: 0 }])
})
