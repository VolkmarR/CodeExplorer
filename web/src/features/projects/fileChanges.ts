import type { PeriodChanges } from '@/features/projects/api'
import { formatCount } from '@/lib/format'

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

/**
 * An upright count's run at the chart's 11px, per character, with room to spare: a digit measured
 * about 5.6px in the browser and a separator less.
 */
const COUNT_CHAR_PX = 6

/** How far an upright count starts from its bar (the text mark's `dy`) plus the gap kept after it. */
const COUNT_GAP_PX = 4 + 8

/**
 * The pixels the longest of `counts` needs past the end of its bar, drawn upright as the chart draws
 * the count of a period over the scale. The chart reserves this above the plot for added counts and
 * between the plot and the axis labels for deleted ones, so the room grows with the number written
 * rather than being fixed for a length a vendor import of a hundred thousand files would overrun.
 */
export function countRoom(counts: number[]): number {
  const longest = Math.max(0, ...counts.map((count) => formatCount(count).length))
  return longest * COUNT_CHAR_PX + COUNT_GAP_PX
}

/** The totals line under the chart, the only place renames appear. */
export function fileChangeTotals(
  periods: PeriodChanges[],
): Pick<PeriodChanges, 'added' | 'deleted' | 'renamed'> {
  const sum = (kind: 'added' | 'deleted' | 'renamed') => periods.reduce((n, p) => n + p[kind], 0)
  return { added: sum('added'), deleted: sum('deleted'), renamed: sum('renamed') }
}
