import { useEffect, useRef } from 'react'
import { useQueryClient } from '@tanstack/react-query'
import type { RefreshState } from '@/lib/api'
import { invalidateProject } from '@/features/projects/queries'

/**
 * Drops what the old index answered, once, when a refresh finishes.
 *
 * A finished refresh is a *transition* and not a state, and that is the whole of this hook. The
 * status reports `Succeeded` from the end of one refresh until the start of the next — for days —
 * so anything keyed on the state alone invalidates the project on every mount that reads it. That
 * was the bug: opening Overview or Settings threw away every file, blame, declaration, search, tree,
 * browse, commit and churn answer already fetched, and the `staleTime: Infinity` those queries set
 * meant nothing because an invalidation is not staleness. Here the previous state is remembered, so
 * a status that was already `Succeeded` when the watcher mounted says nothing happened.
 *
 * It belongs to whatever is mounted on every view of a project, which is the sidebar: a refresh
 * started from the menu there and finished while the reader is on a file has to invalidate exactly
 * as one finished on the project page does. One watcher, one poll, one invalidation per refresh,
 * whatever page is open.
 */
export function useRefreshWatcher(project: string | undefined, state: RefreshState | undefined) {
  const queryClient = useQueryClient()

  // What the last poll said, per project, so that changing project is not itself a transition: a
  // project opened with a long-finished refresh starts at that refresh rather than at nothing.
  const seen = useRef<{ project: string | undefined; state: RefreshState | undefined }>({
    project,
    state,
  })

  useEffect(() => {
    const previous = seen.current
    seen.current = { project, state }
    if (project === undefined || previous.project !== project) return
    // An answer arriving where there was none is not a transition either: the first poll of a page
    // load reports the refresh that finished last week, and that index is exactly the one the cache
    // was filled from.
    if (state === 'Succeeded' && previous.state !== undefined && previous.state !== 'Succeeded')
      void invalidateProject(queryClient, project)
  }, [project, state, queryClient])
}
