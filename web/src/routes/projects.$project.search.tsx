import { createFileRoute } from '@tanstack/react-router'
import { RouteError } from '@/components/RouteError'
import { projectQuery } from '@/features/projects/queries'
import { SearchPage } from '@/features/search/SearchPage'
import { searchQuery } from '@/features/search/queries'
import { validateSearch } from '@/features/search/searchParams'

export const Route = createFileRoute('/projects/$project/search')({
  component: SearchPage,
  errorComponent: RouteError,
  loaderDeps: ({ search }) => search,
  // The project too, because the form offers its repositories to narrow by. An empty query is the
  // page's resting state, not a request: the API would answer it with an explanation, which is the
  // wrong thing to show someone who has not typed anything yet.
  loader: ({ context, deps, params }) =>
    Promise.all([
      context.queryClient.ensureQueryData(projectQuery(params.project)),
      deps.q === ''
        ? undefined
        : context.queryClient.ensureQueryData(searchQuery(params.project, deps)),
    ]),
  validateSearch,
})
