import { createFileRoute } from '@tanstack/react-router'
import { RouteError } from '@/components/RouteError'
import { RiskPage } from '@/features/projects/ProjectPage'
import { projectOverviewQuery, projectQuery } from '@/features/projects/queries'
import { validateOverviewSearch } from '@/lib/urls/overviewParams'

export const Route = createFileRoute('/projects/$project/risk')({
  component: RiskPage,
  errorComponent: RouteError,
  loaderDeps: ({ search }) => search,
  // As the Activity page: the project for the filter bar, and the overview the three pages share.
  loader: ({ context, deps, params }) =>
    Promise.all([
      context.queryClient.ensureQueryData(projectQuery(params.project)),
      context.queryClient.ensureQueryData(projectOverviewQuery(params.project, deps)),
    ]),
  validateSearch: validateOverviewSearch,
})
