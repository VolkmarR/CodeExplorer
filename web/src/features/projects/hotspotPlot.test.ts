import { describe, expect, test } from 'vite-plus/test'
import { plotHotspots, spreadPoints } from '@/features/projects/hotspotPlot'

function file(lines: number, commits: number) {
  return { qualifiedPath: `f${lines}x${commits}`, commits, lines, score: lines * commits }
}

describe('plotHotspots', () => {
  test('spans the lines by whole decades, log scale', () => {
    const plot = plotHotspots([file(3412, 41), file(180, 6)])

    expect(plot.xTicks.map((t) => t.value)).toEqual([100, 1000, 10000])
    expect(plot.xTicks.map((t) => t.at)).toEqual([0, 0.5, 1])
    // A decade is a decade wherever it starts: 316 lines is a quarter of the way from 100 to 10,000.
    expect(plot.points[1]?.x).toBeCloseTo((Math.log10(180) - 2) / 2, 5)
    expect(plotHotspots([file(100, 1), file(316, 1), file(10000, 1)]).points[1]?.x).toBeCloseTo(
      0.25,
      2,
    )
  })

  test('spans the commits from none to a round number above the busiest', () => {
    const plot = plotHotspots([file(3412, 41), file(180, 6)])

    expect(plot.yTicks.map((t) => t.value)).toEqual([0, 50])
    expect(plot.points.map((p) => p.y)).toEqual([41 / 50, 6 / 50])
  })

  test('numbers the points in rank order', () => {
    expect(plotHotspots([file(10, 3), file(20, 1)]).points.map((p) => p.rank)).toEqual([1, 2])
  })

  test('gives one decade to files that all fall in one', () => {
    const plot = plotHotspots([file(100, 1)])

    expect(plot.xTicks.map((t) => t.value)).toEqual([100, 1000])
    expect(plot.points[0]).toEqual({ rank: 1, x: 0, y: 1 })
  })

  test('draws an empty file at the left edge rather than off the plot', () => {
    expect(plotHotspots([file(0, 2), file(50, 1)]).points[0]?.x).toBe(0)
  })
})

function at(rank: number, x: number, y: number) {
  return { rank, x, y }
}

describe('spreadPoints', () => {
  const plot = { height: 200, radius: 10, width: 400 }
  /** The distance between two spread points, in the plot's pixels. */
  const apart = (a: { shownX: number; shownY: number }, b: { shownX: number; shownY: number }) =>
    Math.hypot((a.shownX - b.shownX) * plot.width, (a.shownY - b.shownY) * plot.height)

  test('leaves points that do not touch where they are', () => {
    const spread = spreadPoints([at(1, 0.1, 0.1), at(2, 0.9, 0.9)], plot)

    expect(spread.map((p) => [p.shownX, p.shownY])).toEqual([
      [0.1, 0.1],
      [0.9, 0.9],
    ])
  })

  test('pushes touching points apart until their dots no longer overlap', () => {
    // Five files on one commit count, a few pixels apart: the radix project's shape.
    const spread = spreadPoints(
      [0.5, 0.51, 0.52, 0.53, 0.54].map((x, i) => at(i + 1, x, 0.2)),
      plot,
    )

    for (const [i, a] of spread.entries())
      for (const b of spread.slice(i + 1))
        expect(apart(a, b)).toBeGreaterThanOrEqual(2 * plot.radius)
  })

  test('separates points at the very same place', () => {
    const [a, b] = spreadPoints([at(1, 0.5, 0.5), at(2, 0.5, 0.5)], plot)

    expect(apart(a!, b!)).toBeGreaterThanOrEqual(2 * plot.radius)
  })

  test('keeps the true position beside the shown one, and every dot inside the plot', () => {
    const spread = spreadPoints([at(1, 0, 0), at(2, 0, 0), at(3, 0.01, 0)], plot)

    expect(spread.map((p) => [p.x, p.y])).toEqual([
      [0, 0],
      [0, 0],
      [0.01, 0],
    ])
    for (const p of spread) {
      expect(p.shownX).toBeGreaterThanOrEqual(0)
      expect(p.shownY).toBeGreaterThanOrEqual(0)
    }
  })
})
