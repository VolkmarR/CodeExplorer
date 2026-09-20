import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'

/**
 * One section of the file rail. Its own component because every panel beside the code is the same
 * small card with the same heading, and a second hand-written copy of the shell is the thing that
 * makes one panel sit a few pixels off from the ones above it.
 */
export function RailPanel({
  title,
  count,
  children,
}: {
  title: string
  /**
   * Shown beside the heading where there is a number worth seeing without reading the list. Already
   * written out, because a count that stopped at a ceiling is not a plain number — `500+`.
   */
  count?: string
  children: React.ReactNode
}) {
  return (
    <Card size="sm">
      <CardHeader>
        <CardTitle className="flex items-baseline justify-between gap-2">
          {title}
          {count === undefined ? null : (
            <span className="text-xs font-normal text-muted-foreground tabular-nums">{count}</span>
          )}
        </CardTitle>
      </CardHeader>
      <CardContent>{children}</CardContent>
    </Card>
  )
}
