import { createFileRoute } from '@tanstack/react-router'
import { ProjectPage } from '@/features/projects/ProjectPage'
import { ensureOverview } from '@/features/projects/queries'
import { refreshStatusQuery } from '@/features/refresh/queries'
import { validateOverviewSearch } from '@/lib/urls/overviewParams'

export const Route = createFileRoute('/projects/$project/')({
  component: ProjectPage,
  loaderDeps: ({ search }) => search,
  // What every overview page loads, and the refresh status because the card around this one shows
  // it: a page opened while a refresh is running has to say so straight away rather than after the
  // first poll comes back.
  loader: ({ context, deps, params }) =>
    Promise.all([
      ensureOverview(context.queryClient, params.project, deps),
      context.queryClient.ensureQueryData(refreshStatusQuery(params.project)),
    ]),
  validateSearch: validateOverviewSearch,
})
