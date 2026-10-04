import { isValidElement, type ReactElement, type ReactNode } from 'react'
import { describe, expect, it } from 'vite-plus/test'
import type { OverviewChurn } from '@/features/projects/api'
import { ExcludedNote } from '@/features/projects/ExcludedNote'
import { historyWindow, NO_HISTORY, OverviewCard } from '@/features/projects/OverviewCard'

const churn = (since: string | null, until: string | null): OverviewChurn => ({
  days: 90,
  since,
  until,
  files: [],
})

describe('historyWindow', () => {
  it('is the window where both ends are set, even where the window ranked nothing', () => {
    expect(historyWindow(churn('2026-01-01', '2026-03-31'))).toEqual({
      since: '2026-01-01',
      until: '2026-03-31',
    })
  })

  it('is null where neither is, which is a project with no imported history', () => {
    expect(historyWindow(churn(null, null))).toBeNull()
  })

  it('is null where only one end is set, rather than a window open at the other', () => {
    expect(historyWindow(churn('2026-01-01', null))).toBeNull()
    expect(historyWindow(churn(null, '2026-03-31'))).toBeNull()
  })
})

/**
 * The frame is called as a plain function rather than rendered, as `MatchedLine.test.tsx` does: it
 * holds no state and no hooks, so its element tree is the whole answer. The tree is flattened into
 * the text it would show and the elements it places, which is what a reader of the card sees and
 * survives a change to how the frame nests its parts.
 */
function flatten(node: ReactNode): { text: string; elements: ReactElement[] } {
  const elements: ReactElement[] = []
  const text: string[] = []
  const walk = (n: ReactNode): void => {
    if (typeof n === 'string' || typeof n === 'number') text.push(String(n))
    else if (Array.isArray(n)) n.forEach(walk)
    else if (isValidElement(n)) {
      elements.push(n)
      walk((n.props as { children?: ReactNode }).children)
    }
  }
  walk(node)
  return { elements, text: text.join(' ') }
}

const card = (history: OverviewChurn | undefined) =>
  flatten(
    OverviewCard({
      aside: 'per week',
      children: 'the ranking',
      excluded: { files: 3, project: 'demo', which: 'changed in this window' },
      history,
      note: 'how to read it',
      title: 'Most changed',
    }),
  )

describe('OverviewCard', () => {
  it('draws its title, aside, note, body and excluded note where the history has a window', () => {
    const { text, elements } = card(churn('2026-01-01', '2026-03-31'))
    expect(text).toBe('Most changed per week how to read it the ranking')
    const note = elements.find((e) => e.type === ExcludedNote)
    expect(note?.props).toEqual({ files: 3, project: 'demo', which: 'changed in this window' })
  })

  it('says there is no history in place of everything but its title where there is none', () => {
    const { text, elements } = card(churn(null, null))
    expect(text).toBe(`Most changed ${NO_HISTORY}`)
    expect(elements.some((e) => e.type === ExcludedNote)).toBe(false)
  })

  it('draws its body on a card that is not read from history at all', () => {
    expect(card(undefined).text).toBe('Most changed per week how to read it the ranking')
  })
})
