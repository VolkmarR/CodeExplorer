import { expect, test } from 'vite-plus/test'
import { collect, type Pattern } from '@/highlight/patterns'

/**
 * The collector both language definitions share. Its rules are stated in prose — patterns run in
 * order and the first to claim a span keeps it, a `group` must be the tail of the match — and each
 * of them is something a new pattern can break from a distance.
 */

test('the first pattern to claim a span keeps it', () => {
  const patterns: Pattern[] = [
    { className: 'comment', regex: /\/\/[^\n]*/g },
    { className: 'keyword', regex: /\bclass\b/g },
  ]
  expect(collect('// class', patterns)).toEqual([{ className: 'comment', end: 8, start: 0 }])
  // Reversed, the keyword inside the comment wins and the comment can no longer claim the line.
  expect(collect('// class', patterns.toReversed())).toEqual([
    { className: 'keyword', end: 8, start: 3 },
  ])
})

test('a zero-length match is skipped rather than looped on', () => {
  // Without the `lastIndex++` guard this hangs instead of failing, so it is the one case here that
  // has to be asserted rather than eyeballed.
  expect(collect('bbb', [{ className: 'keyword', regex: /a*/g }])).toEqual([])
  expect(collect('', [{ className: 'keyword', regex: /a*/g }])).toEqual([])
  // A pattern that matches empty in places and non-empty in others keeps only what it matched.
  expect(collect('baab', [{ className: 'keyword', regex: /a*/g }])).toEqual([
    { className: 'keyword', end: 3, start: 1 },
  ])
})

test('a group that matched nothing is skipped, and does not claim the match`s own span', () => {
  const patterns: Pattern[] = [{ className: 'type', group: 1, regex: /class(\s+\w+)?/g }]
  expect(collect('class', patterns)).toEqual([])
  expect(collect('class Widget', patterns)).toEqual([{ className: 'type', end: 12, start: 5 }])
})

test('a group is offset from the end of the match, as its doc requires', () => {
  // `.Name` colours `Name` only, and the range starts after the dot.
  expect(collect('a.Name', [{ className: 'property', group: 1, regex: /\.(\w+)/g }])).toEqual([
    { className: 'property', end: 6, start: 2 },
  ])
})

test('a later pattern may claim a span the earlier one did not overlap', () => {
  // This is what lets the declaration rule colour a name whose keyword a previous rule took.
  const patterns: Pattern[] = [
    { className: 'keyword', regex: /\bclass\b/g },
    { className: 'type', group: 1, regex: /\bclass\s+(\w+)/g },
  ]
  expect(collect('class Widget', patterns)).toEqual([
    { className: 'keyword', end: 5, start: 0 },
    { className: 'type', end: 12, start: 6 },
  ])
})

test('a partly claimed span is rejected whole, not in part', () => {
  const patterns: Pattern[] = [
    { className: 'keyword', regex: /\bclass\b/g },
    { className: 'string', regex: /class Widget/g },
  ]
  expect(collect('class Widget', patterns)).toEqual([{ className: 'keyword', end: 5, start: 0 }])
})

test('the className function is given the match that produced it', () => {
  const ranges = collect('// a\n"b"', [
    {
      className: (match) => (match[0].startsWith('/') ? 'comment' : 'string'),
      regex: /\/\/[^\n]*|"[^"]*"/g,
    },
  ])
  expect(ranges).toEqual([
    { className: 'comment', end: 4, start: 0 },
    { className: 'string', end: 8, start: 5 },
  ])
})

test('a pattern is run from the start of every file, not from where the last one ended', () => {
  // The collector builds a fresh RegExp per run; a shared global pattern would carry `lastIndex`.
  const pattern: Pattern = { className: 'keyword', regex: /\bclass\b/g }
  expect(collect('class', [pattern])).toEqual([{ className: 'keyword', end: 5, start: 0 }])
  expect(collect('class', [pattern])).toEqual([{ className: 'keyword', end: 5, start: 0 }])
})
