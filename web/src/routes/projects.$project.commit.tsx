import { createFileRoute, notFound } from '@tanstack/react-router'
import { RouteError } from '@/components/RouteError'
import { validateCommitSearch } from '@/features/history/commitParams'
import { CommitPage } from '@/features/history/CommitPage'
import { commitQuery } from '@/features/history/queries'

export const Route = createFileRoute('/projects/$project/commit')({
  component: CommitPage,
  errorComponent: RouteError,
  // Only the SHA decides what to load; the origin is for the trail above the page and must not refetch.
  loaderDeps: ({ search }) => ({ sha: search.sha }),
  // A URL with no SHA names no commit, so this is not found — the same reading the file route gives
  // an empty path. A SHA the index does not hold fails in the request instead, with the server's own
  // sentence, which says which project was looked in.
  loader: ({ context, deps, params }) => {
    if (deps.sha === '') throw notFound()

    return context.queryClient.ensureQueryData(commitQuery(params.project, deps.sha))
  },
  validateSearch: validateCommitSearch,
})
