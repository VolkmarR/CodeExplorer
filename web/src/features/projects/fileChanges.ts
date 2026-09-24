import type { MonthChanges } from '@/features/projects/api'

/** How far past the typical bar the scale reaches before an outlier month is drawn broken. */
const OUTLIER_FACTOR = 3

/**
 * The count a full-height bar stands for: the largest bar, unless it is more than three times the
 * median of the non-empty bars, in which case that multiple. A restructure month that added a
 * thousand files then draws broken with its real number beside it, instead of flattening every other
 * month to nothing. Added and deleted share one scale, so the two halves of the chart compare.
 */
export function barScale(months: MonthChanges[]): number {
  const bars = months
    .flatMap((m) => [m.added, m.deleted].filter((n) => n > 0))
    .toSorted((a, b) => a - b)
  if (bars.length === 0) return 1
  const median = bars[Math.floor((bars.length - 1) / 2)]
  return Math.min(bars[bars.length - 1], OUTLIER_FACTOR * median)
}

/** The totals line under the chart, the only place renames appear. */
export function fileChangeTotals(months: MonthChanges[]): {
  added: number
  deleted: number
  renamed: number
} {
  return months.reduce(
    (sum, m) => ({
      added: sum.added + m.added,
      deleted: sum.deleted + m.deleted,
      renamed: sum.renamed + m.renamed,
    }),
    { added: 0, deleted: 0, renamed: 0 },
  )
}
