import type { RecentCall } from '@/features/toolCalls/api'
import { formatSeconds } from '@/lib/format'

// 24-hour on purpose: the list is narrow, and an AM/PM suffix cost the tool name its room.
const clock = new Intl.DateTimeFormat(undefined, {
  hour: '2-digit',
  hourCycle: 'h23',
  minute: '2-digit',
  second: '2-digit',
})

/** The latest calls, newest first: which tool, how long, and whether it failed. Never the arguments. */
export function RecentCalls({ calls }: { calls: RecentCall[] }) {
  return (
    <ol className="space-y-1 font-mono text-xs">
      {calls.map((call) => (
        <li key={call.id} className="flex items-baseline gap-3">
          <span className="shrink-0 tabular-nums text-muted-foreground">
            {clock.format(new Date(call.at))}
          </span>
          <span className="w-12 shrink-0 text-right tabular-nums">
            {formatSeconds(call.seconds)}
          </span>
          <span className="min-w-0 truncate">{call.tool}</span>
          {call.failed ? <span className="text-destructive">failed</span> : null}
        </li>
      ))}
    </ol>
  )
}
