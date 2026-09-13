import { queryOptions } from '@tanstack/react-query'
import { api } from '@/lib/api'
import { refreshKey } from '@/lib/queryKeys'

/**
 * A project's refresh, polled while it runs. Polling is the mechanism ADR-0004 chose: the app scales
 * to zero, so there is no connection to hold open and no SignalR or SSE to hold it with. The
 * interval is set only while there is something to watch, so an idle project page makes no requests.
 */
export function refreshStatusQuery(slug: string) {
  return queryOptions({
    queryFn: () => api.refreshStatus(slug),
    queryKey: refreshKey(slug),
    refetchInterval: (query) => {
      const state = query.state.data?.state
      // A second is short enough that the phase reads as live and long enough that a rebuild of
      // minutes costs a few hundred requests rather than thousands.
      return state === 'Queued' || state === 'Running' ? 1000 : false
    },
  })
}
