import type { Hotspot } from '@/features/projects/api'

/** A ranked file on the plot, at fractions of its width and height from the bottom left. */
export interface PlotPoint {
  /** Its rank, from one, which the list under the plot shows too. */
  rank: number
  x: number
  y: number
}

export interface PlotTick {
  value: number
  /** Where along its axis, as a fraction. */
  at: number
}

export interface HotspotPlot {
  points: PlotPoint[]
  xTicks: PlotTick[]
  yTicks: PlotTick[]
}

/**
 * Where the Hotspots card draws each file: lines at HEAD across on a log scale, commits in the
 * window up on a linear one. The lines are logged because a project's file sizes span decades and a
 * linear axis would crowd every file but the largest against the left edge; the commits are not,
 * because they are what a reader counts off the plot. Both axes start at round numbers, so a tick
 * reads as a value and not as an artefact of the data.
 */
export function plotHotspots(files: Hotspot[]): HotspotPlot {
  const low = Math.floor(Math.min(...files.map(decade)))
  // At least one decade, so files that all fall in one still spread across the plot.
  const high = Math.max(Math.ceil(Math.max(...files.map(decade))), low + 1)
  const top = roundUp(Math.max(...files.map((file) => file.commits)))

  return {
    points: files.map((file, i) => ({
      rank: i + 1,
      x: (decade(file) - low) / (high - low),
      y: file.commits / top,
    })),
    xTicks: Array.from({ length: high - low + 1 }, (_, i) => ({
      value: 10 ** (low + i),
      at: i / (high - low),
    })),
    yTicks: [
      { value: 0, at: 0 },
      { value: top, at: 1 },
    ],
  }
}

/** A point with where it is drawn, which differs from where it belongs only where dots would overlap. */
export interface SpreadPoint extends PlotPoint {
  shownX: number
  shownY: number
}

/** The plot's inner size and the dots' radius, in pixels: overlap is a question of what the eye sees. */
export interface PlotSize {
  width: number
  height: number
  radius: number
}

/** Enough passes for ten dots in one heap; a crowd that has not settled by then is drawn as it stands. */
const SPREAD_PASSES = 200

/** The golden angle, in radians: dots in the very same place leave it in directions that never line up. */
const GOLDEN_ANGLE = Math.PI * (3 - Math.sqrt(5))

/**
 * Where to draw each point so that no two dots overlap, moving each as little as it takes. Files of
 * a similar size with the same number of commits land on one spot, and their numbers then read as
 * one smudge. Each pass pushes every overlapping pair apart along the line between them, half each,
 * until a pass moves nothing; the dots stay inside the plot. Points that already stand clear are
 * left exactly where they are, so a plot without a crowd is drawn as before.
 */
export function spreadPoints(points: PlotPoint[], size: PlotSize): SpreadPoint[] {
  const { width, height, radius } = size
  const spread = points.map((p) => ({ px: p.x * width, py: p.y * height }))
  const clearance = 2 * radius

  for (let pass = 0; pass < SPREAD_PASSES; pass++) {
    let moved = false
    for (let i = 0; i < spread.length; i++)
      for (let j = i + 1; j < spread.length; j++) {
        const a = spread[i]
        const b = spread[j]
        let dx = b.px - a.px
        let dy = b.py - a.py
        let distance = Math.hypot(dx, dy)
        if (distance >= clearance) continue
        if (distance === 0) {
          dx = Math.cos(j * GOLDEN_ANGLE)
          dy = Math.sin(j * GOLDEN_ANGLE)
          distance = 1
        }
        // A hair past the clearance, so that rounding cannot leave a pair touching forever.
        const push = (clearance - Math.hypot(b.px - a.px, b.py - a.py)) / 2 + 0.01
        a.px = clamp(a.px - (push * dx) / distance, width)
        a.py = clamp(a.py - (push * dy) / distance, height)
        b.px = clamp(b.px + (push * dx) / distance, width)
        b.py = clamp(b.py + (push * dy) / distance, height)
        moved = true
      }
    if (!moved) break
  }

  return points.map((p, i) => ({
    ...p,
    shownX: width === 0 ? p.x : spread[i].px / width,
    shownY: height === 0 ? p.y : spread[i].py / height,
  }))
}

function clamp(value: number, max: number) {
  return Math.min(Math.max(value, 0), max)
}

/**
 * A file's lines as a power of ten. An empty file counts as one line and is drawn at the left edge:
 * it has no logarithm, and it is as small as a file gets.
 */
function decade(file: Hotspot): number {
  return Math.log10(Math.max(file.lines, 1))
}

/** The smallest of 1, 2 and 5 times a power of ten that is at least `value`. */
function roundUp(value: number): number {
  if (value <= 1) return 1
  const power = 10 ** Math.floor(Math.log10(value))
  // Ten times the power always reaches the value, so it is the last candidate rather than a fourth.
  return ([1, 2, 5].find((m) => m * power >= value) ?? 10) * power
}
