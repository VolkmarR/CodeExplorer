import { barY, defineChart, ruleY } from '@tanstack/charts'
import { Chart } from '@tanstack/charts/react'
import { scaleBand } from '@tanstack/charts/scales/band'
import { scaleLinear } from '@tanstack/charts/scales/linear'
import { tooltip } from '@tanstack/charts/tooltip'
import type { MinuteCalls } from '@/features/toolCalls/api'
import { formatCount, formatCountOf } from '@/lib/format'

const clock = new Intl.DateTimeFormat(undefined, { hour: '2-digit', minute: '2-digit' })

/** Labelled every quarter of an hour; sixty labels would not fit under sixty bars. */
const LABEL_EVERY = 15

/**
 * Calls per minute over the last hour, with the failed ones drawn over the foot of their bar so a
 * bar reads as its total and its failures at once.
 */
export function MinuteChart({ minutes }: { minutes: MinuteCalls[] }) {
  const top = Math.max(1, ...minutes.map((m) => m.calls))
  const definition = defineChart({
    marks: [
      barY(minutes, {
        x: 'minute',
        y: 'calls',
        key: 'minute',
        fill: 'var(--primary)',
        inset: 0.5,
        radius: 1,
      }),
      barY(
        minutes.filter((m) => m.failed > 0),
        {
          x: 'minute',
          y: 'failed',
          key: (m) => `${m.minute}!`,
          fill: 'var(--destructive)',
          inset: 0.5,
          radius: 1,
        },
      ),
      ruleY([0], { stroke: 'var(--border)' }),
    ],
    scales: {
      x: {
        scale: scaleBand()
          .domain(minutes.map((m) => m.minute))
          .padding(0.1),
        axis: {
          line: false,
          ticks: {
            size: 0,
            values: minutes.flatMap((m, i) => (i % LABEL_EVERY === 0 ? [m.minute] : [])),
            format: (minute: string) => clock.format(new Date(minute)),
          },
        },
      },
      // No value axis: the card's header names the busiest minute, and the tooltip every other one.
      y: { scale: scaleLinear().domain([0, top]), axis: false },
    },
    focus: 'group-x',
    keyboard: false,
    tooltip: {
      use: tooltip,
      formatGroup: (points) => (points[0] ? describe(points[0].datum) : ''),
    },
  })

  return (
    <Chart
      definition={definition}
      height={160}
      className="text-xs text-muted-foreground"
      ariaLabel="Tool calls per minute over the last hour"
    />
  )
}

function describe(m: MinuteCalls) {
  const failed = m.failed > 0 ? `, ${formatCount(m.failed)} failed` : ''
  return `${clock.format(new Date(m.minute))}: ${formatCountOf(m.calls, 'call')}${failed}`
}
