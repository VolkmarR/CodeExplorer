import { createFileRoute } from '@tanstack/react-router'
import { RouteError } from '@/components/RouteError'
import { HistoryPage } from '@/features/history/HistoryPage'
import { validateHistorySearch } from '@/lib/urls/historyParams'
import { commitsQuery } from '@/features/history/queries'
import { projectQuery } from '@/features/projects/queries'

export const Route = createFileRoute('/projects/$project/history')({
  component: HistoryPage,
  errorComponent: RouteError,
  loaderDeps: ({ search }) => search,
  // The project too, because the page offers its repositories to narrow by.
  loader: ({ context, deps, params }) =>
    Promise.all([
      context.queryClient.ensureQueryData(projectQuery(params.project)),
      context.queryClient.ensureQueryData(commitsQuery(params.project, deps)),
    ]),
  validateSearch: validateHistorySearch,
})
