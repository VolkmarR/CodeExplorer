import { createFileRoute } from '@tanstack/react-router'
import { RouteError } from '@/components/RouteError'
import { HistoryPage } from '@/features/history/HistoryPage'
import { validateHistorySearch } from '@/features/history/historyParams'
import { commitsQuery, hotFilesQuery } from '@/features/history/queries'
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
      // The ranking beside the list, primed here so the two arrive together: a panel that said
      // "most changed on this page" while the page under it was still loading would be describing
      // commits nobody can see yet.
      context.queryClient.ensureQueryData(hotFilesQuery(params.project, deps)),
    ]),
  validateSearch: validateHistorySearch,
})
