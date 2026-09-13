import { createFileRoute } from '@tanstack/react-router'
import { RouteError } from '@/components/RouteError'
import { ProjectList } from '@/features/projects/ProjectList'
import { projectsQuery } from '@/features/projects/queries'

export const Route = createFileRoute('/')({
  component: ProjectList,
  errorComponent: RouteError,
  loader: ({ context }) => context.queryClient.ensureQueryData(projectsQuery()),
})
