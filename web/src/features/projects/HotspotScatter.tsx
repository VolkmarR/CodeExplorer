import type { Hotspot } from '@/features/projects/api'
import { plotHotspots } from '@/features/projects/hotspotPlot'
import { formatCount } from '@/lib/format'

/** The drawing's own units; the SVG scales to the card's width and keeps its proportions. */
const WIDTH = 600
const HEIGHT = 220
const LEFT = 36
const RIGHT = 16
const TOP = 16
const BOTTOM = 28
const PLOT_WIDTH = WIDTH - LEFT - RIGHT
const PLOT_HEIGHT = HEIGHT - TOP - BOTTOM

/** A fraction along the lines axis, in the drawing's units. */
function x(at: number) {
  return LEFT + at * PLOT_WIDTH
}

/** A fraction up the commits axis, in the drawing's units, which run down. */
function y(at: number) {
  return TOP + (1 - at) * PLOT_HEIGHT
}

/**
 * The ranked files placed by their lines at HEAD and their commits in the window, each numbered as
 * the list under it numbers them. The upper right quarter is shaded as the "big and busy" corner:
 * half way along each axis rather than a threshold on the score, because the axes are all the reader
 * can check it against. Only the ranked files are drawn — the page reads the top of the ranking and
 * nothing else — so the plot shows how the top ten differ, not where they sit in the project.
 */
export function HotspotScatter({ files }: { files: Hotspot[] }) {
  const plot = plotHotspots(files)
  return (
    <svg viewBox={`0 0 ${WIDTH} ${HEIGHT}`} className="w-full text-xs">
      {/* The list under the plot holds the same numbers, so the drawing only needs naming. */}
      <title>The ranked files by lines at HEAD (log scale) and commits in the window</title>
      <rect
        x={x(0.5)}
        y={y(1)}
        width={PLOT_WIDTH / 2}
        height={PLOT_HEIGHT / 2}
        className="fill-primary/5 stroke-primary/40"
        strokeDasharray="4 3"
      />
      <text x={x(1) - 6} y={y(1) + 14} textAnchor="end" className="fill-primary">
        big and busy
      </text>
      <path d={`M ${x(0)} ${y(1)} V ${y(0)} H ${x(1)}`} fill="none" className="stroke-border" />
      {plot.yTicks.map((tick) => (
        <text
          key={tick.value}
          x={LEFT - 6}
          y={y(tick.at) + 4}
          textAnchor="end"
          className="fill-muted-foreground tabular-nums"
        >
          {formatCount(tick.value)}
        </text>
      ))}
      {plot.xTicks.map((tick) => (
        <text
          key={tick.value}
          x={x(tick.at)}
          y={HEIGHT - BOTTOM + 14}
          textAnchor="middle"
          className="fill-muted-foreground tabular-nums"
        >
          {formatCount(tick.value)}
        </text>
      ))}
      <text x={LEFT + 6} y={TOP - 4} className="fill-muted-foreground">
        commits
      </text>
      <text x={x(1)} y={HEIGHT - 2} textAnchor="end" className="fill-muted-foreground">
        lines at HEAD, log scale
      </text>
      {/* Drawn lowest rank first, so where two points overlap the higher-ranked one is on top. */}
      {plot.points.toReversed().map((point) => (
        <g key={point.rank} transform={`translate(${x(point.x)} ${y(point.y)})`}>
          <circle r={10} className="fill-primary stroke-card" strokeWidth={2} />
          <text y={4} textAnchor="middle" className="fill-primary-foreground font-semibold">
            {point.rank}
          </text>
        </g>
      ))}
    </svg>
  )
}
