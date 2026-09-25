import { useMemo } from 'react'
import { barX, defineChart, stack } from '@tanstack/charts'
import { Chart } from '@tanstack/charts/react'
import { scaleBand } from '@tanstack/charts/scales/band'
import { scaleLinear } from '@tanstack/charts/scales/linear'
import { tooltip } from '@tanstack/charts/tooltip'
import type { IndexOverview } from '@/features/projects/api'
import { ExcludedNote } from '@/features/projects/ExcludedNote'
import { formatCount } from '@/lib/format'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'

/** The languages drawn by name; every one after them is folded into "Other". */
const NAMED = 5

/**
 * One colour per named language, in order, and a grey for the rest. Hues far enough apart to tell
 * the segments apart, at a lightness that reads on both the light and the dark ground.
 */
const COLOURS = [
  'var(--primary)',
  'oklch(0.7 0.13 180)',
  'oklch(0.68 0.16 55)',
  'oklch(0.62 0.14 300)',
  'oklch(0.66 0.15 350)',
]
const OTHER_COLOUR = 'var(--muted-foreground)'

interface Segment {
  name: string
  files: number
  lines: number
  colour: string
}

/**
 * What the project is written in, by lines: one bar split between the five largest languages and
 * everything else, because the shares are read against each other and the widest segment answers
 * "what is this project written in" before any number is read.
 */
export function LanguageShares({
  project,
  overview,
  excluded,
}: {
  project: string
  overview: IndexOverview
  /** Files at HEAD the excluded paths left out, if any were. */
  excluded: number | undefined
}) {
  const bar = useMemo(() => {
    const segments: Segment[] = overview.languages.slice(0, NAMED).map((language, i) => ({
      colour: COLOURS[i],
      files: language.files,
      lines: language.lines,
      name: language.name,
    }))
    const rest = overview.languages.slice(NAMED)
    if (rest.length > 0)
      segments.push({
        colour: OTHER_COLOUR,
        files: rest.reduce((sum, language) => sum + language.files, 0),
        lines: rest.reduce((sum, language) => sum + language.lines, 0),
        name: 'Other',
      })
    const total = segments.reduce((sum, segment) => sum + segment.lines, 0)

    const definition = defineChart({
      marks: [
        barX(segments, {
          x: 'lines',
          y: () => 'all',
          color: 'name',
          key: 'name',
          layout: stack({ offset: 'normalize' }),
        }),
      ],
      scales: { x: { scale: scaleLinear }, y: { scale: scaleBand } },
      color: { domain: segments.map((s) => s.name), range: segments.map((s) => s.colour) },
      guides: false,
      margin: 0,
      keyboard: false,
      tooltip: {
        use: tooltip,
        format: (point) =>
          `${point.datum.name}: ${formatCount(point.datum.files)} files, ${formatCount(point.datum.lines)} lines`,
      },
    })
    return { segments, total, definition }
  }, [overview.languages])

  return (
    <Card>
      <CardHeader>
        <CardTitle>Languages</CardTitle>
      </CardHeader>
      <CardContent className="space-y-3">
        {bar.total > 0 ? (
          <>
            <div className="overflow-hidden rounded-full">
              <Chart
                definition={bar.definition}
                height={10}
                ariaLabel="Lines at HEAD by language"
              />
            </div>
            <ul className="flex flex-wrap gap-x-4 gap-y-1 text-xs text-muted-foreground">
              {bar.segments.map((segment) => (
                <li key={segment.name} className="flex items-center gap-1.5">
                  <span
                    aria-hidden
                    className="size-2.5 rounded-xs bg-(--swatch)"
                    style={{ '--swatch': segment.colour } as React.CSSProperties}
                  />
                  {segment.name} {Math.round((100 * segment.lines) / bar.total)} %
                </li>
              ))}
            </ul>
          </>
        ) : (
          <p className="text-sm text-muted-foreground">No lines indexed at HEAD.</p>
        )}
        <ExcludedNote project={project} files={excluded} what="the files at HEAD" />
      </CardContent>
    </Card>
  )
}
