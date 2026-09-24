import { createFileRoute } from '@tanstack/react-router'
import { RouteError } from '@/components/RouteError'
import { ProjectPage } from '@/features/projects/ProjectPage'
import { projectOverviewQuery, projectQuery } from '@/features/projects/queries'
import { refreshStatusQuery } from '@/features/refresh/queries'
import { validateOverviewSearch } from '@/lib/urls/overviewParams'

export const Route = createFileRoute('/projects/$project/')({
  component: ProjectPage,
  errorComponent: RouteError,
  loaderDeps: ({ search }) => search,
  // The project and the refresh status because the card around the page shows both, and a page
  // opened while a refresh is running has to say so straight away rather than after the first poll
  // comes back. The project also names the repositories the filter bar offers. Then the overview,
  // which is this page: it is the heaviest read here, and there is no longer a branch deciding
  // whether it is wanted.
  loader: ({ context, deps, params }) =>
    Promise.all([
      context.queryClient.ensureQueryData(projectQuery(params.project)),
      context.queryClient.ensureQueryData(refreshStatusQuery(params.project)),
      context.queryClient.ensureQueryData(projectOverviewQuery(params.project, deps)),
    ]),
  validateSearch: validateOverviewSearch,
})
