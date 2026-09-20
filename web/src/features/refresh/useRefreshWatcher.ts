import { useEffect, useRef } from 'react'
import { useQueryClient } from '@tanstack/react-query'
import type { RefreshStatus } from '@/lib/api'
import { invalidateProject } from '@/features/projects/queries'

/** What the watcher remembers between polls: which refresh it has already acted on. */
export interface SeenRefresh {
  project: string | undefined
  /** `null` while none has finished, `undefined` before the first answer arrives. */
  finishedAt: string | null | undefined
}

/**
 * Whether `next` is a refresh that finished and has not been acted on yet.
 *
 * A finished refresh is identified by *which* one it is and not by the state alone. The status
 * reports `Succeeded` from the end of one refresh until the start of the next — for days — so
 * anything keyed on the state invalidates the project on every mount that reads it. That was the
 * bug. But keying on the `Running -> Succeeded` edge instead is not enough either: the status only
 * polls while a refresh is running, so one started anywhere else — the MCP tool, a second tab,
 * another operator — can begin and end unobserved, and the edge never arrives. `finishedAt` is the
 * identity of the refresh that finished, so a different one is a different answer to invalidate
 * for, whether this tab watched it happen or only found it afterwards.
 *
 * Separated from the hook so it can be tested as the table of cases it is, without a renderer.
 */
export function isNewlyFinished(
  previous: SeenRefresh,
  next: SeenRefresh,
  state: RefreshStatus['state'] | undefined,
) {
  // Changing project is not a transition: a project opened with a long-finished refresh starts at
  // that refresh rather than at nothing.
  if (next.project === undefined || previous.project !== next.project) return false

  // A refresh that failed leaves the old index in place, so there is nothing to drop. The next one
  // to succeed still invalidates, because `finishedAt` will differ from the failed one's.
  if (state !== 'Succeeded' || next.finishedAt == null) return false

  // An answer arriving where there was none is not a transition: the first poll of a page load
  // reports the refresh that finished last week, and that index is exactly the one the cache was
  // filled from.
  if (previous.finishedAt === undefined) return false

  return previous.finishedAt !== next.finishedAt
}

/**
 * Drops what the old index answered, once, when a refresh finishes.
 *
 * It belongs to whatever is mounted on every view of a project, which is the sidebar: a refresh
 * started from the menu there and finished while the reader is on a file has to invalidate exactly
 * as one finished on the project page does. One watcher, one poll, one invalidation per refresh,
 * whatever page is open.
 *
 * A refresh nothing here started is caught the next time the status is asked for at all — the query
 * sets no `staleTime`, so mounting the frame and returning to the tab both refetch it. That is the
 * recovery point, and it is why this reacts to a refresh it has not seen before rather than only to
 * one it watched finish.
 */
export function useRefreshWatcher(project: string | undefined, status: RefreshStatus | undefined) {
  const queryClient = useQueryClient()
  const finishedAt = status?.finishedAt

  const seen = useRef<SeenRefresh>({ finishedAt, project })

  useEffect(() => {
    const previous = seen.current
    const next = { finishedAt, project }
    seen.current = next
    if (isNewlyFinished(previous, next, status?.state) && project !== undefined)
      void invalidateProject(queryClient, project)
  }, [project, finishedAt, status?.state, queryClient])
}
