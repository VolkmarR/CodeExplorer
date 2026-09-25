import { useMemo } from 'react'
import { defineChart, dot, link, rect, text } from '@tanstack/charts'
import { Chart } from '@tanstack/charts/react'
import { scaleLinear } from '@tanstack/charts/scales/linear'
import { tooltip } from '@tanstack/charts/tooltip'
import type { Hotspot } from '@/features/projects/api'
import { plotHotspots, spreadPoints, type PlotTick } from '@/features/projects/hotspotPlot'
import { formatCount } from '@/lib/format'

/**
 * The "big and busy" quarter: half way along each axis rather than a threshold on the score, because
 * the axes are all the reader can check it against.
 */
const CORNER = [{ x1: 0.5, x2: 1, y1: 0.5, y2: 1 }]

/** The dot mark's id, which is how focus tells a file's point from the corner's and the labels'. */
const DOTS = 'files'

const HEIGHT = 240
const RADIUS = 10

/**
 * Fixed rather than measured, because spreading the dots needs the plot's size in pixels before the
 * chart lays itself out. Wide enough on the left for "100,000" and the axis title.
 */
const MARGIN = { bottom: 40, left: 56, right: 16, top: 12 }

/**
 * The ranked files placed by their lines at HEAD and their commits in the window, each numbered as
 * the list under it numbers them. Only the ranked files are drawn — the page reads the top of the
 * ranking and nothing else — so the plot shows how the top ten differ, not where they sit in the
 * project.
 *
 * Both axes run over fractions from `plotHotspots`, which owns the log scale and the round ends, so
 * the chart maps 0 to 1 linearly and labels the ticks with the values they stand for. Dots that would
 * overlap are spread apart (`spreadPoints`), and a moved dot keeps a thin line to where it belongs.
 */
export function HotspotScatter({ files }: { files: Hotspot[] }) {
  const definition = useMemo(() => {
    const plot = plotHotspots(files)
    return defineChart(
      ({ width }) => {
        const spread = spreadPoints(plot.points, {
          height: HEIGHT - MARGIN.top - MARGIN.bottom,
          radius: RADIUS,
          width: Math.max(width - MARGIN.left - MARGIN.right, 0),
        })
        // Drawn lowest rank first, so the higher-ranked dot is on top wherever two still touch. Each
        // carries its file's path, which the tooltip names.
        const points = spread
          .map(({ rank, x, y, shownX, shownY }) => ({
            path: files[rank - 1].qualifiedPath,
            rank,
            shownX,
            shownY,
            x,
            y,
          }))
          .toReversed()
        // Rows and keys of their own for everything drawn beside the file dots: the focus ring is
        // painted on every node that shares the focused dot's row or key, so a pin or a label sharing
        // them lit up as a second focused dot.
        const pins = points.flatMap((p) =>
          p.shownX !== p.x || p.shownY !== p.y
            ? [{ key: `pin:${p.rank}`, shownX: p.shownX, shownY: p.shownY, x: p.x, y: p.y }]
            : [],
        )
        const labels = points.map((p) => ({
          key: `label:${p.rank}`,
          rank: p.rank,
          shownX: p.shownX,
          shownY: p.shownY,
        }))
        return {
          marks: [
            rect(CORNER, {
              x1: 'x1',
              x2: 'x2',
              y1: 'y1',
              y2: 'y2',
              fill: 'var(--primary)',
              fillOpacity: 0.06,
              stroke: 'var(--primary)',
              strokeWidth: 1,
              inset: 0,
            }),
            text([{ x: 1, y: 1 }], {
              x: 'x',
              y: 'y',
              text: () => 'big and busy',
              anchor: 'end',
              dx: -6,
              dy: 12,
              fill: 'var(--primary)',
              fontSize: 12,
            }),
            // Where each moved file belongs: a pin at the true place and a line to its dot.
            link(pins, {
              x1: 'x',
              y1: 'y',
              x2: 'shownX',
              y2: 'shownY',
              key: 'key',
              stroke: 'var(--primary)',
              strokeOpacity: 0.6,
              strokeWidth: 1,
            }),
            dot(pins, { x: 'x', y: 'y', key: 'key', r: 2, fill: 'var(--primary)' }),
            dot(points, {
              id: DOTS,
              x: 'shownX',
              y: 'shownY',
              key: 'rank',
              r: RADIUS,
              fill: 'var(--primary)',
              stroke: 'var(--card)',
              strokeWidth: 2,
            }),
            text(labels, {
              x: 'shownX',
              y: 'shownY',
              key: 'key',
              text: 'rank',
              fill: 'var(--primary-foreground)',
              fontSize: 12,
              fontWeight: 600,
            }),
          ],
          scales: {
            x: {
              scale: scaleLinear().domain([0, 1]),
              axis: { label: 'lines at HEAD, log scale', ticks: ticks(plot.xTicks) },
            },
            y: {
              scale: scaleLinear().domain([0, 1]),
              axis: { label: 'commits', ticks: ticks(plot.yTicks) },
            },
          },
          margin: MARGIN,
        }
      },
      {
        // Hovering a dot names its file. Only the dots take focus: the default strategy would also stop
        // on the corner, the pins and the labels, which name nothing. No keyboard stop, because the
        // list under the plot carries the same files and is where they are opened.
        focus: {
          resolve: (candidates, { x, y, maxDistance }) => {
            let nearest: (typeof candidates)[number] | undefined
            let best = maxDistance
            for (const point of candidates) {
              const distance = Math.hypot(point.x - x, point.y - y)
              if (point.markId === DOTS && distance <= best) {
                nearest = point
                best = distance
              }
            }
            return nearest ? [nearest] : []
          },
          group: (_, { point }) => [point],
          navigation: () => [],
        },
        keyboard: false,
        tooltip: {
          use: tooltip,
          // Every point focus resolves is a file's, but the rows are typed as any mark's.
          format: ({ datum }) => ('path' in datum ? `${datum.rank}. ${datum.path}` : ''),
        },
      },
    )
  }, [files])

  return (
    <Chart
      definition={definition}
      height={HEIGHT}
      className="text-xs text-muted-foreground"
      // The list under the plot holds the same numbers, so the drawing only needs naming.
      ariaLabel="The ranked files by lines at HEAD (log scale) and commits in the window"
    />
  )
}

/** An axis's ticks at the fractions `plotHotspots` chose, labelled with the values they stand for. */
function ticks(at: PlotTick[]) {
  const labels = new Map(at.map((tick) => [tick.at, formatCount(tick.value)]))
  return {
    values: at.map((tick) => tick.at),
    format: (value: number) => labels.get(value) ?? '',
  }
}
