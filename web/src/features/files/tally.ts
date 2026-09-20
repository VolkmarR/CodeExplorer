import { formatCount } from '@/lib/format'

/**
 * The number beside a rail panel's heading. A list that stopped at a server ceiling is the one case
 * where its length is not the answer to "how many", so it is shown as a floor rather than a total.
 */
export function tally(length: number, capped: boolean) {
  return capped ? `${formatCount(length)}+` : formatCount(length)
}
