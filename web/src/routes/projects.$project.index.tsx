import { createFileRoute } from '@tanstack/react-router'
import { RouteError } from '@/components/RouteError'
import { ProjectPage } from '@/features/projects/ProjectPage'
import { projectQuery } from '@/features/projects/queries'
import { refreshStatusQuery } from '@/features/refresh/queries'

export const Route = createFileRoute('/projects/$project/')({
  component: ProjectPage,
  errorComponent: RouteError,
  // Both, because a page opened while a refresh is running has to show it straight away rather than
  // after the first poll comes back.
  loader: ({ context, params }) =>
    Promise.all([
      context.queryClient.ensureQueryData(projectQuery(params.project)),
      context.queryClient.ensureQueryData(refreshStatusQuery(params.project)),
    ]),
})
