import { Card, CardContent, CardHeader } from '@/components/ui/card'
import { Separator } from '@/components/ui/separator'
import { cn } from '@/lib/utils'

/**
 * The frame every view sits in: one card carrying the view's name, what it can say about itself,
 * and its own controls, with the view's content nested inside the same border.
 *
 * It exists so the eye lands in the same place on all six pages. Before it, each page was a set of
 * loose sections on the background and the reader had to find the title again on every one; the
 * card is also what gives a result list or a table somewhere to nest, instead of floating.
 *
 * `title` is the page's `h1` and there is exactly one per view, so the heading order of every page
 * in this app is decided here rather than six times.
 */
export function PageCard({
  title,
  hint,
  badges,
  actions,
  tabs,
  children,
  className,
}: {
  title: string
  /** One clause on what this view answers from. Beside the title, not under it. */
  hint?: React.ReactNode
  badges?: React.ReactNode
  actions?: React.ReactNode
  /** Rendered under the header and above the content, inside the card's border. */
  tabs?: React.ReactNode
  children: React.ReactNode
  className?: string
}) {
  return (
    <Card className={cn('gap-0', className)}>
      <CardHeader className="flex flex-wrap items-center gap-x-3 gap-y-2">
        <h1 className="text-lg font-semibold tracking-tight">{title}</h1>
        {hint ? <span className="text-sm text-muted-foreground">{hint}</span> : null}
        {badges}
        {actions ? <div className="ml-auto flex items-center gap-2">{actions}</div> : null}
      </CardHeader>
      {tabs ? <div className="mt-4 px-(--card-spacing)">{tabs}</div> : null}
      <Separator className="mt-4" />
      <CardContent className="pt-(--card-spacing)">{children}</CardContent>
    </Card>
  )
}
