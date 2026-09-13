import { createFileRoute } from '@tanstack/react-router'
import { RouteError } from '@/components/RouteError'
import { SearchPage } from '@/features/search/SearchPage'
import { searchQuery } from '@/features/search/queries'
import { validateSearch } from '@/features/search/searchParams'

export const Route = createFileRoute('/projects/$project/search')({
  component: SearchPage,
  errorComponent: RouteError,
  loaderDeps: ({ search }) => search,
  // An empty query is the page's resting state, not a request: the API would answer it with an
  // explanation, which is the wrong thing to show someone who has not typed anything yet.
  loader: ({ context, deps, params }) =>
    deps.q === ''
      ? undefined
      : context.queryClient.ensureQueryData(searchQuery(params.project, deps)),
  validateSearch,
})
