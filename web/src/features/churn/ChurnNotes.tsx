import type { Churn } from '@/features/churn/api'
import { formatCount } from '@/lib/format'

/**
 * What the ranking above does not cover. Both notes are about the same mistake — reading a partial
 * ranking as a whole one — so they live together rather than as two conditions in the view.
 *
 * `hidden` is new with the filters (#161) and is the one a reader can act on: a narrowed ranking
 * looks exactly like an unfiltered one, and "the busiest file here is X" is a sentence people
 * repeat. It is said only under a ranking; where the filter hid everything, the empty message says
 * so instead, because a reader with no rows needs the reason and not a footnote.
 */
export function ChurnNotes({ ranking }: { ranking: Churn }) {
  const hidden = ranking.files.length > 0 ? ranking.hidden : 0
  const without = ranking.withoutHistory

  return (
    <>
      {hidden > 0 ? (
        <p className="text-xs text-muted-foreground">
          {formatCount(hidden)} other {hidden === 1 ? 'path' : 'paths'} changed in this window and{' '}
          {hidden === 1 ? 'is' : 'are'} filtered out of this ranking.
        </p>
      ) : null}

      {without.length > 0 ? (
        <p className="text-xs text-muted-foreground">
          No history was imported for {without.join(', ')}, so nothing from{' '}
          {without.length === 1 ? 'it' : 'those'} can appear here however much{' '}
          {without.length === 1 ? 'it' : 'they'} changed.
        </p>
      ) : null}
    </>
  )
}
