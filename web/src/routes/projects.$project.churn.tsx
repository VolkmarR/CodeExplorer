import { createFileRoute } from '@tanstack/react-router'
import { RouteError } from '@/components/RouteError'
import { ChurnPage } from '@/features/churn/ChurnPage'
import { validateChurnSearch } from '@/features/churn/churnParams'
import { churnQuery } from '@/features/churn/queries'
import { projectQuery } from '@/features/projects/queries'

export const Route = createFileRoute('/projects/$project/churn')({
  component: ChurnPage,
  errorComponent: RouteError,
  loaderDeps: ({ search }) => search,
  // The project too, because the page offers its repositories to narrow by.
  loader: ({ context, deps, params }) =>
    Promise.all([
      context.queryClient.ensureQueryData(projectQuery(params.project)),
      context.queryClient.ensureQueryData(churnQuery(params.project, deps)),
    ]),
  validateSearch: validateChurnSearch,
})
