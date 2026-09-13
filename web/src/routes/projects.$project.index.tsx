import { createFileRoute } from '@tanstack/react-router'
import { RouteError } from '@/components/RouteError'
import { ProjectPage } from '@/features/projects/ProjectPage'
import { projectQuery } from '@/features/projects/queries'

export const Route = createFileRoute('/projects/$project/')({
  component: ProjectPage,
  errorComponent: RouteError,
  loader: ({ context, params }) =>
    context.queryClient.ensureQueryData(projectQuery(params.project)),
})
