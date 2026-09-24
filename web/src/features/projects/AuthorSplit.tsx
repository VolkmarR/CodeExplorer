import type { CSSProperties } from 'react'
import { cn } from '@/lib/utils'

/**
 * The bar's segments as its legend names them: the first, second and third author darkest first, so
 * the first reads as the owner, and everyone else as the track behind them.
 */
export const AUTHOR_SEGMENTS = [
  { label: 'first author', className: 'bg-primary' },
  { label: 'second', className: 'bg-primary/60' },
  { label: 'third', className: 'bg-primary/30' },
  { label: 'everyone else', className: 'bg-muted' },
] as const

/**
 * One file's commits split by author: the first three authors' shares, then everyone else as the
 * rest of the bar. A short first segment is a file nobody owns.
 */
export function AuthorSplit({ shares }: { shares: [number, number, number] }) {
  return (
    <div
      className={cn(
        'flex h-2.5 w-36 shrink-0 overflow-hidden rounded-sm',
        AUTHOR_SEGMENTS[3].className,
      )}
    >
      {/* A missing author is left out rather than drawn at no width, where its divider would still
          show as a sliver. */}
      {shares.map(
        (share, i) =>
          share > 0 && (
            <div
              key={AUTHOR_SEGMENTS[i].label}
              className={cn(
                'h-full w-(--share) border-r border-background',
                AUTHOR_SEGMENTS[i].className,
              )}
              style={{ '--share': `${share * 100}%` } as CSSProperties}
            />
          ),
      )}
    </div>
  )
}
