import type { ReactNode } from 'react'

/**
 * One row of a `RankedList`. Centred rather than aligned on the baseline, as the churn page's rows
 * are, because a row may hold a bar or a rank badge, which has no text baseline to align on.
 */
export function RankedRow({ children }: { children: ReactNode }) {
  return <li className="flex items-center gap-3 px-4 py-2">{children}</li>
}
