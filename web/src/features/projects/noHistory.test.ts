import { describe, expect, it } from 'vite-plus/test'
import type { OverviewChurn } from '@/features/projects/api'
import { historyWindow } from '@/features/projects/noHistory'

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
