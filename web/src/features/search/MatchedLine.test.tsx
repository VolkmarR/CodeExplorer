import type { ReactElement } from 'react'
import { expect, test } from 'vite-plus/test'
import { MatchedLine } from '@/features/search/MatchedLine'
import { matchPattern, matchRanges, type MatchRange } from '@/features/search/matchRanges'

/**
 * Syntax colour and match marking are two layers over one string, and the cut between them is a
 * two-cursor walk that can only go wrong by dropping or duplicating characters — which renders as a
 * line that still looks like a line. So every case here re-joins the pieces and compares them to the
 * source text, on top of asserting where the marks landed.
 *
 * The component is called as a plain function rather than rendered: it holds no state and no hooks,
 * so its element tree is the whole answer and a DOM would add nothing to read it with.
 */
interface Piece {
  text: string
  className: string
  matched: boolean
}

function pieces(text: string, ranges: MatchRange[], language = 'csharp'): Piece[] {
  const nodes = MatchedLine({ language, ranges, text }) as ReactElement[]
  return nodes.map((node) => read(node))
}

function read(node: ReactElement): Piece {
  const outer = (node.props as { children: ReactElement | string }).children
  const matched = typeof outer !== 'string' && outer.type === 'mark'
  const span = matched ? (outer.props as { children: ReactElement | string }).children : outer
  if (typeof span === 'string') return { className: '', matched, text: span }
  const props = span.props as { children: string; className: string }
  return { className: props.className, matched, text: props.children }
}

function rangesOf(text: string, query: string): MatchRange[] {
  return matchRanges(text, matchPattern({ caseSensitive: false, page: 1, q: query, regex: false }))
}

function joined(list: Piece[]): string {
  return list.map((piece) => piece.text).join('')
}

function markedText(list: Piece[]): string {
  return list
    .filter((piece) => piece.matched)
    .map((piece) => piece.text)
    .join('')
}

test('a line with no match is left as the highlighter tokenized it', () => {
  const text = 'Console.WriteLine(x);'
  const list = pieces(text, [])
  expect(joined(list)).toBe(text)
  expect(list.some((piece) => piece.matched)).toBe(false)
  expect(list.map((piece) => piece.text)).toEqual(['Console', '.', 'WriteLine', '(x);'])
})

test('a match spanning three tokens is cut into three marked pieces, each keeping its colour', () => {
  const text = 'Console.WriteLine(x);'
  const list = pieces(text, rangesOf(text, 'Console.WriteLine'))
  expect(joined(list)).toBe(text)
  expect(markedText(list)).toBe('Console.WriteLine')
  const inside = list.filter((piece) => piece.matched)
  expect(inside.map((piece) => piece.text)).toEqual(['Console', '.', 'WriteLine'])
  // The colour survives the cut: the mark is drawn around the coloured span, not instead of it.
  expect(inside[0].className).toContain('th-type')
  expect(inside[2].className).toContain('th-function')
})

test('a token holding half a match is split at the boundary and keeps one class on both halves', () => {
  const text = 'Console.WriteLine(x);'
  // A regex search, which is the mode that can land a match inside an identifier: text mode asks
  // for word boundaries and would never cut `Console` open.
  const list = pieces(
    text,
    matchRanges(text, matchPattern({ caseSensitive: false, page: 1, q: 'nsol', regex: true })),
  )
  expect(joined(list)).toBe(text)
  expect(markedText(list)).toBe('nsol')
  const head = list[0]
  const inside = list[1]
  expect(head).toEqual({ className: head.className, matched: false, text: 'Co' })
  expect(inside.text).toBe('nsol')
  expect(inside.className).toBe(head.className)
  expect(list[2]).toEqual({ className: head.className, matched: false, text: 'e' })
})

test('a match ending exactly on a token boundary does not swallow the next token', () => {
  const text = 'Console.WriteLine(x);'
  const list = pieces(text, rangesOf(text, 'Console'))
  expect(joined(list)).toBe(text)
  expect(markedText(list)).toBe('Console')
  expect(list.filter((piece) => piece.matched)).toHaveLength(1)
})

test('two matches in one line are marked apart rather than as one run', () => {
  const text = 'Run(); Run();'
  const list = pieces(text, rangesOf(text, 'Run'))
  expect(joined(list)).toBe(text)
  expect(list.filter((piece) => piece.matched).map((piece) => piece.text)).toEqual(['Run', 'Run'])
})

test('a match starting at the first character is marked, not skipped', () => {
  const text = 'var total = count + 1;'
  const list = pieces(text, rangesOf(text, 'var'))
  expect(joined(list)).toBe(text)
  expect(list[0]).toEqual({ className: list[0].className, matched: true, text: 'var' })
  expect(list[0].className).toContain('th-keyword')
})

test('a match running to the end of the line closes without a trailing empty piece', () => {
  const text = 'x = total'
  const list = pieces(text, rangesOf(text, 'total'))
  expect(joined(list)).toBe(text)
  expect(list.some((piece) => piece.text === '')).toBe(false)
  expect(markedText(list)).toBe('total')
  expect(list.at(-1)?.matched).toBe(true)
})

test('the whole line as one match marks every piece and still re-joins', () => {
  const text = 'Console.WriteLine(x);'
  const list = pieces(text, [{ end: text.length, start: 0 }])
  expect(joined(list)).toBe(text)
  expect(list.every((piece) => piece.matched)).toBe(true)
})

test('plaintext, where the line is one leaf, is cut the same way', () => {
  const text = 'a plain line of prose'
  const list = pieces(text, rangesOf(text, 'plain'), 'plaintext')
  expect(joined(list)).toBe(text)
  expect(list.map((piece) => [piece.text, piece.matched])).toEqual([
    ['a ', false],
    ['plain', true],
    [' line of prose', false],
  ])
})
