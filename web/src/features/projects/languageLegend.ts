import { formatPercent } from '@/lib/format'

/**
 * A language's share as the Languages legend prints it. Rounded to a whole percent, except at the two
 * ends, where rounding would deny what the bar shows: a language too small to reach 1% is "<1%" and
 * not "0%", which reads as a language with no lines, and one just short of all of them is ">99%" and
 * not "100%" beside the others it shares the bar with.
 */
export function legendPercent(share: number): string {
  if (share > 0 && share < 0.005) return '<1%'
  if (share < 1 && share >= 0.995) return '>99%'
  return formatPercent(share)
}
