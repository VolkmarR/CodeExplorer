import type { CSSProperties } from 'react'
import { cn } from '@/lib/utils'

/**
 * A thin bar filled to `share` percent, for a row read against the rows around it. The width goes
 * through a custom property because a computed width is the one value a utility class cannot hold.
 */
export function ShareBar({ share, className }: { share: number; className?: string }) {
  return (
    <div className={cn('h-1.5 overflow-hidden rounded-full bg-muted', className)}>
      <div
        className="h-full w-(--share) rounded-full bg-primary"
        style={{ '--share': `${share}%` } as CSSProperties}
      />
    </div>
  )
}
