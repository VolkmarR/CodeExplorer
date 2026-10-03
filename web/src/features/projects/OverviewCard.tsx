import type { ComponentProps, ReactNode } from 'react'
import type { OverviewChurn } from '@/features/projects/api'
import { ExcludedNote } from '@/features/projects/ExcludedNote'
import { historyWindow, NO_HISTORY } from '@/features/projects/noHistory'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'

/**
 * The frame every card on the overview pages is drawn in: a title with an optional aside beside it,
 * an intro note, the card's own body, and the excluded-paths note under it. A card read from history
 * passes Most changed's section as `history`, and where that holds no window the frame says
 * {@link NO_HISTORY} in place of everything but the title, so no history card can forget to — or
 * decide it differently from the others.
 *
 * The note, the body and the excluded note are direct children of `CardContent`, a flex column whose
 * gap spaces them, so a card's body is written as a fragment of its parts rather than one wrapper.
 */
export function OverviewCard({
  title,
  aside,
  history,
  note,
  excluded,
  className,
  children,
}: {
  title: string
  /** Beside the title, saying what the body covers: a span, a period or a repository. */
  aside?: ReactNode
  /** Most changed's section, on a card read from history: whether there was any at all. */
  history?: OverviewChurn
  /** How to read the card, said once above the body. */
  note?: ReactNode
  /** How many files the excluded paths kept out, said under the body. */
  excluded?: ComponentProps<typeof ExcludedNote>
  /** The content's own type and spacing, as a list of mono paths or a list of names sets it. */
  className?: string
  children: ReactNode
}) {
  const noHistory = history !== undefined && !historyWindow(history)
  // The aside says what the body covers, so it goes where the body does.
  const shownAside = noHistory ? null : aside
  return (
    <Card>
      <CardHeader
        className={shownAside ? 'flex flex-row items-baseline justify-between' : undefined}
      >
        <CardTitle>{title}</CardTitle>
        {shownAside ? <span className="text-xs text-muted-foreground">{shownAside}</span> : null}
      </CardHeader>
      <CardContent className={className}>
        {noHistory ? (
          <p className="text-sm text-muted-foreground">{NO_HISTORY}</p>
        ) : (
          <>
            {note ? <p className="pb-3 text-xs text-muted-foreground">{note}</p> : null}
            {children}
            {excluded ? <ExcludedNote {...excluded} /> : null}
          </>
        )}
      </CardContent>
    </Card>
  )
}
