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
  // An empty file is drawn at the left edge: it has no logarithm, and it is as small as a file gets.
  const decades = files.map((file) => Math.log10(Math.max(file.lines, 1)))
  const low = Math.floor(Math.min(...decades))
  // At least one decade, so files that all fall in one still spread across the plot.
  const high = Math.max(Math.ceil(Math.max(...decades)), low + 1)
  const top = roundUp(Math.max(...files.map((file) => file.commits)))
  const across = (decade: number) => Math.max(0, (decade - low) / (high - low))

  return {
    points: files.map((file, i) => ({
      rank: i + 1,
      x: across(decades[i] ?? low),
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

/** The smallest of 1, 2 and 5 times a power of ten that is at least `value`. */
function roundUp(value: number): number {
  if (value <= 1) return 1
  const power = 10 ** Math.floor(Math.log10(value))
  return [1, 2, 5, 10].map((m) => m * power).find((candidate) => candidate >= value) ?? 10 * power
}
