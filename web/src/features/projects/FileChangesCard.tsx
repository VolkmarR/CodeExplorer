import { useMemo } from 'react'
import { barY, defineChart, ruleY, text } from '@tanstack/charts'
import { Chart } from '@tanstack/charts/react'
import { scaleBand } from '@tanstack/charts/scales/band'
import { scaleLinear } from '@tanstack/charts/scales/linear'
import { tooltip } from '@tanstack/charts/tooltip'
import type { ChangePeriod, OverviewFileChanges, PeriodChanges } from '@/features/projects/api'
import { barScale, fileChangeTotals } from '@/features/projects/fileChanges'
import { NO_HISTORY } from '@/features/projects/noHistory'
import { formatCount } from '@/lib/format'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'

const title = <CardTitle>Files added and deleted</CardTitle>

/** What the header calls one bar. */
const PER: Record<ChangePeriod, string> = { Day: 'per day', Month: 'per month', Week: 'per week' }

/**
 * Whether the codebase is growing or being consolidated (#214): the files added drawn above the line
 * and the files deleted below it, over the filter bar's window, a bar per day, week or month by its
 * length. The scale is capped (`barScale`), so an outlier draws broken with its real count beside it.
 * Renames are not drawn: a move neither grows nor shrinks the code, so they appear only in the totals.
 */
export function FileChangesCard({ changes }: { changes: OverviewFileChanges }) {
  if (changes.periods.length === 0) {
    return (
      <Card>
        <CardHeader>{title}</CardHeader>
        <CardContent>
          <p className="text-sm text-muted-foreground">{NO_HISTORY}</p>
        </CardContent>
      </Card>
    )
  }

  const totals = fileChangeTotals(changes.periods)
  return (
    <Card>
      <CardHeader className="flex flex-row items-baseline justify-between">
        {title}
        <span className="text-xs text-muted-foreground">{PER[changes.period]}</span>
      </CardHeader>
      <CardContent>
        <PeriodChart changes={changes} />
        {/* The exact counts for a reader who cannot see the bars; the tooltip has them for one who can. */}
        <ol className="sr-only" aria-label={`Files added and deleted ${PER[changes.period]}`}>
          {changes.periods.map((p) => (
            <li key={p.start}>{describePeriod(p, changes.period)}</li>
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

/** One half of a period's column: the added half is drawn up from the line, the deleted half down. */
interface Half {
  period: PeriodChanges
  key: string
  added: boolean
  count: number
  /** The drawn length, signed and capped at the scale; `count` is the real one. */
  length: number
}

/**
 * The periods as columns on one shared scale, so the two halves compare. A half over the scale is
 * drawn at full length with its real count at the tip, the usual mark for a broken axis.
 */
function PeriodChart({ changes }: { changes: OverviewFileChanges }) {
  const definition = useMemo(() => {
    const { period, periods } = changes
    const scale = barScale(periods)
    const halves: Half[] = periods.flatMap((p) =>
      [true, false].map((added) => {
        const count = added ? p.added : p.deleted
        return {
          added,
          count,
          key: `${p.start}${added ? '+' : '-'}`,
          length: (added ? 1 : -1) * Math.min(count, scale),
          period: p,
        }
      }),
    )
    const broken = halves.filter((half) => half.count > scale)
    const ticks = axisTicks(periods, period)

    return defineChart({
      marks: [
        barY(halves, {
          x: (half) => half.period.start,
          y: 'length',
          key: 'key',
          fill: (half) => (half.added ? 'var(--primary)' : 'var(--destructive)'),
          inset: 0.5,
          radius: 1,
        }),
        ruleY([0], { stroke: 'var(--border)' }),
        text(broken, {
          x: (half) => half.period.start,
          y: 'length',
          key: 'key',
          text: (half) => formatCount(half.count),
          // Upright, running away from the line: neighbouring outliers are one bar apart, and
          // their counts side by side would read as one number.
          rotate: -90,
          anchor: (half) => (half.added ? 'start' : 'end'),
          dy: (half) => (half.added ? -4 : 4),
          fill: 'var(--foreground)',
          fontSize: 11,
        }),
      ],
      scales: {
        x: {
          scale: scaleBand()
            .domain(periods.map((p) => p.start))
            .padding(0.1),
          axis: { line: false, ticks: { size: 0, ...ticks } },
        },
        y: {
          scale: scaleLinear().domain([-scale, scale]),
          axis: false,
        },
      },
      // Room above the line for the upright counts of the periods drawn broken.
      margin: { top: broken.some((half) => half.added) ? 36 : 8 },
      focus: 'group-x',
      keyboard: false,
      tooltip: {
        use: tooltip,
        formatGroup: (points) => (points[0] ? describePeriod(points[0].datum.period, period) : ''),
      },
    })
  }, [changes])

  return (
    <Chart
      definition={definition}
      height={170}
      className="text-xs text-muted-foreground"
      ariaLabel={`Files added and deleted ${PER[changes.period]}`}
    />
  )
}

const dayFormat = new Intl.DateTimeFormat('en-US', {
  day: 'numeric',
  month: 'short',
  timeZone: 'UTC',
})
const fullDayFormat = new Intl.DateTimeFormat('en-US', { dateStyle: 'medium', timeZone: 'UTC' })
const shortMonthFormat = new Intl.DateTimeFormat('en-US', { month: 'short', timeZone: 'UTC' })
const monthFormat = new Intl.DateTimeFormat('en-US', {
  month: 'long',
  timeZone: 'UTC',
  year: 'numeric',
})

/** A period's start as a date. The server sends a calendar day in UTC, so it is read as one. */
function startOf(p: PeriodChanges | string) {
  return new Date(`${typeof p === 'string' ? p : p.start}T00:00:00Z`)
}

/** Month names read comfortably under up to this many monthly bars; beyond, only the years do. */
const NAMED_MONTHS = 14

/**
 * Which bars get a label, and what it says: a few landmarks rather than one per bar, which would not
 * fit. Days are labelled on Mondays, weeks at the first week of each month, and months by name where
 * there are few of them and by year at each January where there are many.
 */
function axisTicks(periods: PeriodChanges[], period: ChangePeriod) {
  const at = (keep: (date: Date, i: number) => boolean) =>
    periods.flatMap((p, i) => (keep(startOf(p), i) ? [p.start] : []))
  switch (period) {
    case 'Day':
      return {
        values: at((d) => d.getUTCDay() === 1),
        format: (s: string) => dayFormat.format(startOf(s)),
      }
    case 'Week':
      return {
        values: at((d, i) => i === 0 || d.getUTCDate() <= 7),
        format: (s: string) => shortMonthFormat.format(startOf(s)),
      }
    case 'Month':
      return periods.length <= NAMED_MONTHS
        ? {
            values: periods.map((p) => p.start),
            format: (s: string) => shortMonthFormat.format(startOf(s)),
          }
        : { values: at((d) => d.getUTCMonth() === 0), format: (s: string) => s.slice(0, 4) }
  }
}

function describePeriod(p: PeriodChanges, period: ChangePeriod) {
  const date = startOf(p)
  const when =
    period === 'Day'
      ? fullDayFormat.format(date)
      : period === 'Week'
        ? `Week of ${fullDayFormat.format(date)}`
        : monthFormat.format(date)
  return `${when}: +${formatCount(p.added)} added, −${formatCount(p.deleted)} deleted, ${formatCount(p.renamed)} renamed`
}
