import { describe, expect, test } from 'vite-plus/test'
import { plotHotspots } from '@/features/projects/hotspotPlot'

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
