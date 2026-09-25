import type { PeriodChanges } from '@/features/projects/api'

/** How far past the typical bar the scale reaches before an outlier period is drawn broken. */
const OUTLIER_FACTOR = 3

/**
 * The count a full-height bar stands for: the largest bar, unless it is more than three times the
 * median of the non-empty bars, in which case that multiple. A restructure that added a thousand
 * files then draws broken with its real number beside it, instead of flattening every other period to
 * nothing. Added and deleted share one scale, so the two halves of the chart compare.
 */
export function barScale(periods: PeriodChanges[]): number {
  const bars = periods
    .flatMap((p) => [p.added, p.deleted].filter((n) => n > 0))
    .toSorted((a, b) => a - b)
  if (bars.length === 0) return 1
  const median = bars[Math.floor((bars.length - 1) / 2)]
  return Math.min(bars[bars.length - 1], OUTLIER_FACTOR * median)
}

/** The totals line under the chart, the only place renames appear. */
export function fileChangeTotals(
  periods: PeriodChanges[],
): Pick<PeriodChanges, 'added' | 'deleted' | 'renamed'> {
  const sum = (kind: 'added' | 'deleted' | 'renamed') => periods.reduce((n, p) => n + p[kind], 0)
  return { added: sum('added'), deleted: sum('deleted'), renamed: sum('renamed') }
}
