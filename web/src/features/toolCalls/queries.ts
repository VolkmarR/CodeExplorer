import { queryOptions } from '@tanstack/react-query'
import { fetchToolActivity, fetchToolCalls } from '@/features/toolCalls/api'
import { toolActivityKey, toolCallsKey } from '@/lib/queryKeys'

/**
 * How often an open page re-reads the counts. Slow on purpose: the page is a glance at what agents
 * have been doing, not a monitor, and the page's Refresh button is there for anyone who wants now.
 * TanStack Query does not poll a hidden tab, so a page left open in the background costs nothing.
 */
const REFRESH_EVERY = 5 * 60_000

export function toolCallsQuery(slug: string) {
  return queryOptions({
    queryFn: () => fetchToolCalls(slug),
    queryKey: toolCallsKey(slug),
    refetchInterval: REFRESH_EVERY,
  })
}

export function toolActivityQuery() {
  return queryOptions({
    queryFn: () => fetchToolActivity(),
    queryKey: toolActivityKey,
    refetchInterval: REFRESH_EVERY,
  })
}
