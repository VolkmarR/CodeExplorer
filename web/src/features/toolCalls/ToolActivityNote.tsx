import { useQuery } from '@tanstack/react-query'
import { toolActivityQuery } from '@/features/toolCalls/queries'
import { formatCountOf, formatTime } from '@/lib/format'

/**
 * One line on a project's card in the list: whether agents have been using it. Not a suspense query,
 * so the list never waits for it or fails with it. The counts are a nicety beside the list, and a
 * project no agent has called since the server started shows nothing at all.
 */
export function ToolActivityNote({ project }: { project: string }) {
  const { data } = useQuery(toolActivityQuery())
  const activity = data?.find((a) => a.project === project)
  if (activity === undefined) return null

  return (
    <span className="text-xs text-muted-foreground tabular-nums">
      {formatCountOf(activity.callsLastHour, 'tool call')} in the last hour · last{' '}
      {formatTime(activity.lastCall)}
    </span>
  )
}
