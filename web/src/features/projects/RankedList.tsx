import type { ReactNode } from 'react'
import { cn } from '@/lib/utils'

/**
 * A ranking on an overview card, one `RankedRow` per file or pair, in a bordered, divided box. It
 * matches the box `ChurnList` draws, which Most changed shows, without sharing it: the churn feature
 * would have to import from this one, which already imports `ChurnList` from it, and its rows align
 * on the text baseline where these centre.
 */
export function RankedList({
  className,
  children,
}: {
  /** Placement only, as the space above a list drawn under a chart. */
  className?: string
  children: ReactNode
}) {
  return (
    <ol className={cn('divide-y rounded-lg border bg-card font-mono text-xs', className)}>
      {children}
    </ol>
  )
}
