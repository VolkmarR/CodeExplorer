import { useSuspenseQuery } from '@tanstack/react-query'
import { useParams } from '@tanstack/react-router'
import { RefreshCw } from 'lucide-react'
import { PageCard } from '@/components/PageCard'
import { MinuteChart } from '@/features/toolCalls/MinuteChart'
import { RecentCalls } from '@/features/toolCalls/RecentCalls'
import { toolCallsQuery } from '@/features/toolCalls/queries'
import { ToolTable } from '@/features/toolCalls/ToolTable'
import { formatCount, formatPercent, formatSeconds, formatTime } from '@/lib/format'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'

/**
 * What agents have asked this project over MCP since the server started. The server keeps the counts
 * in memory only, so a restart empties the page, and the header says since when it has counted.
 */
export function ToolCallsPage() {
  const { project } = useParams({ from: '/projects/$project/tool-calls' })
  const { data, dataUpdatedAt, isFetching, refetch } = useSuspenseQuery(toolCallsQuery(project))

  const busiest = Math.max(0, ...data.perMinute.map((m) => m.calls))
  const tiles = [
    { label: 'Calls in the last hour', value: formatCount(data.callsLastHour) },
    {
      label: 'Failed in the last hour',
      value:
        data.callsLastHour === 0
          ? '—'
          : `${formatCount(data.failedLastHour)} (${formatPercent(data.failedLastHour / data.callsLastHour)})`,
    },
    {
      label: 'p95, last 200 calls',
      value: data.p95Seconds === null ? '—' : formatSeconds(data.p95Seconds),
    },
  ]

  return (
    <PageCard
      title="Tool calls"
      hint={`What agents asked since ${formatTime(data.since)}`}
      actions={
        <Button variant="outline" size="sm" onClick={() => void refetch()} disabled={isFetching}>
          <RefreshCw />
          Refresh
        </Button>
      }
    >
      <div className="space-y-6">
        <dl className="grid gap-3 sm:grid-cols-3">
          {tiles.map((tile) => (
            <div key={tile.label} className="rounded-lg border px-4 py-3">
              <dt className="text-xs text-muted-foreground">{tile.label}</dt>
              <dd className="text-2xl font-semibold tabular-nums">{tile.value}</dd>
            </div>
          ))}
        </dl>

        <Card>
          <CardHeader className="flex flex-row items-baseline justify-between">
            <CardTitle>Calls per minute</CardTitle>
            <span className="text-xs text-muted-foreground">
              last hour · busiest minute {formatCount(busiest)}
            </span>
          </CardHeader>
          <CardContent>
            <MinuteChart minutes={data.perMinute} />
          </CardContent>
        </Card>

        {data.tools.length === 0 ? (
          <p className="text-sm text-muted-foreground">
            No agent has called this project since the server started.
          </p>
        ) : (
          <div className="grid gap-6 lg:grid-cols-3">
            <Card className="lg:col-span-2">
              <CardHeader>
                <CardTitle>By tool</CardTitle>
              </CardHeader>
              <CardContent>
                <ToolTable tools={data.tools} />
              </CardContent>
            </Card>
            <Card>
              <CardHeader>
                <CardTitle>Latest calls</CardTitle>
              </CardHeader>
              <CardContent>
                <RecentCalls calls={data.recent} />
              </CardContent>
            </Card>
          </div>
        )}

        <p className="text-xs text-muted-foreground">
          Counted in the server&apos;s memory and started over when it restarts. Read again every
          five minutes; last read {formatTime(new Date(dataUpdatedAt).toISOString())}.
        </p>
      </div>
    </PageCard>
  )
}
