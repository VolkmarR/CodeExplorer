import { describe, expect, it } from 'vite-plus/test'
import type { MonthChanges } from '@/features/projects/api'
import { barScale, fileChangeTotals } from '@/features/projects/fileChanges'

const month = (added: number, deleted: number, renamed = 0): MonthChanges => ({
  year: 2026,
  month: 1,
  added,
  deleted,
  renamed,
})

describe('barScale', () => {
  it('is the largest bar where nothing stands out', () => {
    expect(barScale([month(4, 2), month(6, 3), month(5, 0)])).toBe(6)
  })

  it('caps an outlier month so the others keep their height', () => {
    expect(barScale([month(4, 2), month(6, 3), month(5, 1), month(900, 0)])).toBe(12)
  })

  it('is at least one where every month is empty', () => {
    expect(barScale([month(0, 0), month(0, 0)])).toBe(1)
  })
})

describe('fileChangeTotals', () => {
  it('sums every kind apart', () => {
    expect(fileChangeTotals([month(4, 2, 1), month(6, 3, 5)])).toEqual({
      added: 10,
      deleted: 5,
      renamed: 6,
    })
  })
})
