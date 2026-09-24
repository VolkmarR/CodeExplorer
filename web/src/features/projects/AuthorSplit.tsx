import type { CSSProperties } from 'react'

/** The first, second and third author's segment, darkest first, so the first reads as the owner. */
export const AUTHOR_SEGMENTS = ['bg-primary', 'bg-primary/60', 'bg-primary/30'] as const

/**
 * One file's commits split by author: the first three authors' shares, then everyone else as the
 * rest of the bar. A short first segment is a file nobody owns.
 */
export function AuthorSplit({ shares }: { shares: [number, number, number] }) {
  return (
    <div className="flex h-2.5 w-36 shrink-0 overflow-hidden rounded-sm bg-muted">
      {/* A missing author is left out rather than drawn at no width, where its divider would still
          show as a sliver. */}
      {shares.map(
        (share, i) =>
          share > 0 && (
            <div
              key={AUTHOR_SEGMENTS[i]}
              className={`h-full w-(--share) border-r border-background ${AUTHOR_SEGMENTS[i]}`}
              style={{ '--share': `${share * 100}%` } as CSSProperties}
            />
          ),
      )}
    </div>
  )
}
