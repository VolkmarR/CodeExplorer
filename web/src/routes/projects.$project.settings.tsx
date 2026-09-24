import { createFileRoute } from '@tanstack/react-router'
import { RouteError } from '@/components/RouteError'
import { ProjectSettings } from '@/features/projects/ProjectSettings'
import { excludedPathsQuery, projectQuery } from '@/features/projects/queries'
import { refreshStatusQuery } from '@/features/refresh/queries'

export const Route = createFileRoute('/projects/$project/settings')({
  component: ProjectSettings,
  errorComponent: RouteError,
  // The project and the refresh status, and not the overview: this page is where an operator goes to
  // do something about a project, including one whose overview is the read that failed.
  loader: ({ context, params }) =>
    Promise.all([
      context.queryClient.ensureQueryData(projectQuery(params.project)),
      context.queryClient.ensureQueryData(refreshStatusQuery(params.project)),
      context.queryClient.ensureQueryData(excludedPathsQuery(params.project)),
    ]),
})
