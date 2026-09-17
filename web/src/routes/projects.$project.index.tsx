import { createFileRoute } from '@tanstack/react-router'
import { RouteError } from '@/components/RouteError'
import { ProjectPage } from '@/features/projects/ProjectPage'
import { validateProjectSearch } from '@/features/projects/projectParams'
import { projectOverviewQuery, projectQuery } from '@/features/projects/queries'
import { refreshStatusQuery } from '@/features/refresh/queries'

export const Route = createFileRoute('/projects/$project/')({
  component: ProjectPage,
  errorComponent: RouteError,
  loaderDeps: ({ search }) => search,
  // The project and the refresh status on either tab: a page opened while a refresh is running has
  // to show it straight away rather than after the first poll comes back, and it is the one thing
  // happening to the project whichever half is open.
  //
  // The overview only for the tab that shows it. Not a saving — it is the page's heaviest read, but
  // that is what a loader is for — but a separation: the overview is the one read here that can
  // fail on its own, and settings is where an operator goes to do something about that.
  loader: ({ context, deps, params }) =>
    Promise.all([
      context.queryClient.ensureQueryData(projectQuery(params.project)),
      context.queryClient.ensureQueryData(refreshStatusQuery(params.project)),
      deps.tab === 'overview'
        ? context.queryClient.ensureQueryData(projectOverviewQuery(params.project))
        : undefined,
    ]),
  validateSearch: validateProjectSearch,
})
