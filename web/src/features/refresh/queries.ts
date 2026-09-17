import { queryOptions } from '@tanstack/react-query'
import type { RefreshStatus } from '@/lib/api'
import { api } from '@/lib/api'
import { refreshKey } from '@/lib/queryKeys'

/**
 * Whether there is a rebuild in flight. Two of the five states mean "not finished", and everything
 * that cares — the poll below, the progress panel, the sidebar's dot, the two buttons that must not
 * start a second one — asked the question by naming both. A state added server-side would have had
 * to be found in five places; now it is found here.
 */
export function isRefreshRunning(status: RefreshStatus | Pick<RefreshStatus, 'state'> | undefined) {
  return status?.state === 'Queued' || status?.state === 'Running'
}

/** How often to ask again while one is running, in milliseconds. */
const INTERVALS = {
  /**
   * The project page, which is the only view that shows the step counter: a second is short enough
   * that the phase reads as live, and long enough that a rebuild of minutes costs a few hundred
   * requests rather than thousands.
   */
  page: 1000,
  /**
   * The frame, which shows only that something is running and what it is called. It is mounted on
   * every view of a project, including one a reader leaves open while a rebuild takes minutes, so
   * it asks a fifth as often for a word that changes every few seconds at most.
   */
  frame: 5000,
} as const

/**
 * A project's refresh, polled while it runs. Polling is the mechanism ADR-0004 chose: the app scales
 * to zero, so there is no connection to hold open and no SignalR or SSE to hold it with. The
 * interval is set only while there is something to watch, so an idle project page makes no requests.
 *
 * `watcher` names who is asking, because the two want different rates and they share a cache entry:
 * whichever is mounted decides, and on the project page — where both are — the faster one wins,
 * which is the one showing the counter.
 */
export function refreshStatusQuery(slug: string, watcher: keyof typeof INTERVALS = 'page') {
  return queryOptions({
    queryFn: () => api.refreshStatus(slug),
    queryKey: refreshKey(slug),
    refetchInterval: (query) => (isRefreshRunning(query.state.data) ? INTERVALS[watcher] : false),
  })
}
