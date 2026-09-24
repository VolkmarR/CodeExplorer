import type { CSSProperties } from 'react'
import type { MonthChanges, OverviewFileChanges } from '@/features/projects/api'
import { barScale, fileChangeTotals } from '@/features/projects/fileChanges'
import { NO_HISTORY } from '@/features/projects/noHistory'
import { formatCount, formatMonth } from '@/lib/format'
import { cn } from '@/lib/utils'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'

const title = <CardTitle>Files added and deleted</CardTitle>

/**
 * Whether the codebase is growing or being consolidated (#214): per month, the files added drawn
 * above the line and the files deleted below it, over the two years to the newest commit. The scale
 * is capped (`barScale`), so an outlier month draws broken with its real count beside it. Renames
 * are not drawn: a move neither grows nor shrinks the code, so they appear only in the totals line.
 */
export function FileChangesCard({ changes }: { changes: OverviewFileChanges }) {
  if (changes.months.length === 0) {
    return (
      <Card>
        <CardHeader>{title}</CardHeader>
        <CardContent>
          <p className="text-sm text-muted-foreground">{NO_HISTORY}</p>
        </CardContent>
      </Card>
    )
  }

  const scale = barScale(changes.months)
  const totals = fileChangeTotals(changes.months)
  return (
    <Card>
      <CardHeader className="flex flex-row items-baseline justify-between">
        {title}
        <span className="text-xs text-muted-foreground">
          last {changes.months.length} months, whatever the window
        </span>
      </CardHeader>
      <CardContent>
        <ol aria-label="Files added and deleted per month" className="flex items-stretch gap-px">
          {changes.months.map((m) => (
            <Month key={`${m.year}-${m.month}`} month={m} scale={scale} />
          ))}
        </ol>
        <p className="pt-3 text-xs tabular-nums">
          <span className="text-primary">+{formatCount(totals.added)} added</span>
          {' · '}
          <span className="text-destructive">−{formatCount(totals.deleted)} deleted</span>
          {' · '}
          <span className="text-muted-foreground">{formatCount(totals.renamed)} renamed</span>
        </p>
        <p className="pt-2 text-xs text-muted-foreground">
          Renames are the moves git detected. A move that also changed more than half of a file
          falls outside rename detection and counts as a delete plus an add.
        </p>
      </CardContent>
    </Card>
  )
}

/** One month's column: the added bar grows up from the line, the deleted bar down from it. */
function Month({ month, scale }: { month: MonthChanges; scale: number }) {
  const label = `${formatMonth(month.year, month.month)}:+${formatCount(month.added)} added, −${formatCount(month.deleted)} deleted, ${formatCount(month.renamed)} renamed`
  return (
    <li className="flex min-w-0 flex-1 flex-col" title={label}>
      <span className="sr-only">{label}</span>
      <Bar count={month.added} scale={scale} up />
      <div className="h-px bg-border" />
      <Bar count={month.deleted} scale={scale} up={false} />
      <span className="h-5 overflow-visible pt-1 text-xs whitespace-nowrap text-muted-foreground">
        {month.month === 1 ? month.year : ''}
      </span>
    </li>
  )
}

/** How each half of a column is drawn: the added half grows up from the line, the deleted half down. */
const ABOVE = {
  bar: 'rounded-t-xs bg-primary',
  broken: 'mt-4 grow',
  column: 'justify-end',
  cut: 'top-1',
  tip: 'top-0',
}
const BELOW = {
  bar: 'rounded-b-xs bg-destructive',
  broken: 'mb-4 grow',
  column: '',
  cut: 'bottom-1',
  tip: 'bottom-0',
}

/**
 * Half a column. A count over the scale fills it and draws a gap across the bar near its end, the
 * usual mark for a broken axis, with the real count at the tip.
 */
function Bar({ count, scale, up }: { count: number; scale: number; up: boolean }) {
  const broken = count > scale
  const side = up ? ABOVE : BELOW
  return (
    <div className={cn('relative flex h-16 flex-col', side.column)}>
      {broken && (
        <span
          className={cn(
            'absolute inset-x-0 z-10 text-center text-xs leading-none tabular-nums',
            side.tip,
          )}
        >
          {formatCount(count)}
        </span>
      )}
      <div
        className={cn('relative', side.bar, broken ? side.broken : 'h-(--share)')}
        style={{ '--share': `${(100 * count) / scale}%` } as CSSProperties}
      >
        {broken && <span className={cn('absolute inset-x-0 h-0.5 -skew-y-12 bg-card', side.cut)} />}
      </div>
    </div>
  )
}
