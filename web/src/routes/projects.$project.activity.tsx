import { createFileRoute } from '@tanstack/react-router'
import { RouteError } from '@/components/RouteError'
import { ActivityPage } from '@/features/projects/ProjectPage'
import { projectOverviewQuery, projectQuery } from '@/features/projects/queries'
import { validateOverviewSearch } from '@/lib/urls/overviewParams'

export const Route = createFileRoute('/projects/$project/activity')({
  component: ActivityPage,
  errorComponent: RouteError,
  loaderDeps: ({ search }) => search,
  // The project because the filter bar offers its repositories, and the overview this page is a
  // third of: the same entry the other two pages read, so moving between them is a cache hit.
  loader: ({ context, deps, params }) =>
    Promise.all([
      context.queryClient.ensureQueryData(projectQuery(params.project)),
      context.queryClient.ensureQueryData(projectOverviewQuery(params.project, deps)),
    ]),
  validateSearch: validateOverviewSearch,
})
