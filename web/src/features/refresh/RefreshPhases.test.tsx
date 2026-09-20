import type { ReactElement } from 'react'
import { expect, test } from 'vite-plus/test'
import { RefreshPhases } from '@/features/refresh/RefreshPhases'
import type { PhaseCost } from '@/lib/api'

/**
 * Two invariants this panel states in prose. A row's key is counted rather than positional, because
 * the same step and text legitimately repeat in one timeline and React would otherwise reuse a row
 * for a different phase. And the seconds have two branches nothing else exercises: a phase too fast
 * to show at two decimals, which must not be reported as free, and one past a minute, where the
 * decimals have stopped being the comparison.
 *
 * Read by calling the component as a function: it holds no state and no hooks, so its element tree
 * is the whole answer.
 */
function cost(step: number, phase: string, seconds: number): PhaseCost {
  return { phase, seconds, step }
}

function panel(phases: PhaseCost[], wallSeconds: number | null = null): ReactElement | null {
  return RefreshPhases({ phases, wallSeconds }) as ReactElement | null
}

/** Every string in the tree, with nested components called so their own text is included. */
function texts(node: unknown, into: string[] = []): string[] {
  if (node === null || node === undefined || typeof node === 'boolean') return into
  if (typeof node === 'string' || typeof node === 'number') {
    into.push(String(node))
    return into
  }
  if (Array.isArray(node)) {
    for (const child of node) texts(child, into)
    return into
  }
  const element = node as ReactElement
  if (typeof element.type === 'function') {
    return texts((element.type as (props: unknown) => unknown)(element.props), into)
  }
  return texts((element.props as { children?: unknown }).children, into)
}

/** The keys of the row list, which is what the counted key exists to make unique. */
function rowKeys(node: ReactElement): (string | null)[] {
  const found: (string | null)[] = []
  walk(node, found)
  return found
}

function walk(node: unknown, into: (string | null)[]) {
  if (Array.isArray(node)) {
    for (const child of node) walk(child, into)
    return
  }
  if (node === null || typeof node !== 'object') return
  const element = node as ReactElement
  if (element.type === 'li') into.push(element.key)
  walk((element.props as { children?: unknown }).children, into)
}

test('a phase repeated in one timeline gets its own key, not a colliding one', () => {
  const node = panel([
    cost(3, 'full-text index', 1),
    cost(3, 'full-text index', 2),
    cost(4, 'full-text index', 3),
  ])
  const keys = rowKeys(node!)
  expect(keys).toHaveLength(3)
  expect(new Set(keys).size).toBe(3)
  // The step is part of the key, so the same text at a different step is a different row too.
  expect(keys).toEqual(['3-full-text index-0', '3-full-text index-1', '4-full-text index-0'])
})

test('seconds carry two decimals up to a minute', () => {
  expect(texts(panel([cost(1, 'clone', 3.456)]))).toContain('3.46s')
  expect(texts(panel([cost(1, 'clone', 59.994)]))).toContain('59.99s')
})

test('a phase too fast for two decimals is reported as under, never as free', () => {
  // The server keeps milliseconds so that a phase which happened is not reported as costing nothing;
  // rounding it to `0.00s` here would undo that.
  const shown = texts(panel([cost(1, 'schema', 0.004)]))
  expect(shown).toContain('<0.01s')
  expect(shown).not.toContain('0.00s')
})

test('a phase that truly cost nothing is still `0.00s` and not `<0.01s`', () => {
  expect(texts(panel([cost(1, 'schema', 0)]))).toContain('0.00s')
})

test('past a minute the decimals go, because whole seconds are the comparison there', () => {
  expect(texts(panel([cost(3, 'full-text index', 65.4)]))).toContain('65s')
  expect(texts(panel([cost(3, 'full-text index', 60)]))).toContain('60s')
  // Just under the boundary is still the two-decimal form.
  expect(texts(panel([cost(3, 'full-text index', 59.9)]))).toContain('59.90s')
})

test('what no phase accounts for is named rather than folded into the last bar', () => {
  const shown = texts(panel([cost(1, 'clone', 2)], 30)).join('')
  expect(shown).toContain('Spent 2.00s of 30.00s across 1 phases')
  expect(shown).toContain('28.00s outside them')
})

test('a gap too small to explain is not named', () => {
  // Below a twentieth of a second there is nothing but rounding and the queue wait to report.
  expect(texts(panel([cost(1, 'clone', 2)], 2.02)).join('')).not.toContain('outside them')
})

test('a refresh still running says so far, because there is no whole yet', () => {
  expect(texts(panel([cost(1, 'clone', 2)], null)).join('')).toContain(
    '2.00s across 1 phases so far',
  )
})

test('no phases draws no panel at all', () => {
  expect(panel([])).toBe(null)
})
