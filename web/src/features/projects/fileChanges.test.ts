import { describe, expect, it } from 'vite-plus/test'
import type { PeriodChanges } from '@/features/projects/api'
import { barScale, countRoom, fileChangeTotals } from '@/features/projects/fileChanges'

const month = (added: number, deleted: number, renamed = 0): PeriodChanges => ({
  start: '2026-01-01',
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

describe('countRoom', () => {
  it('grows with the longest count drawn, so a longer count never runs into the axis labels', () => {
    expect(countRoom([999])).toBeLessThan(countRoom([12345]))
    expect(countRoom([12345])).toBeLessThan(countRoom([1234567]))
  })

  it('is sized by the longest of several counts', () => {
    expect(countRoom([5, 12345, 40])).toBe(countRoom([12345]))
  })

  // Measured in the browser: "2,723" upright is 28px tall and starts 4px past its bar, so the 36px
  // reserved before cleared "Sep" by 2px. Eight is the clearance a reader sees as a gap.
  it('leaves "2,723" at least 8px clear of the axis labels', () => {
    expect(countRoom([2723])).toBeGreaterThanOrEqual(28 + 4 + 8)
  })
})
