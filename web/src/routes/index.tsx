import { createFileRoute } from '@tanstack/react-router'
import { ProjectList } from '@/features/projects/ProjectList'
import { projectsQuery } from '@/features/projects/queries'

export const Route = createFileRoute('/')({
  component: ProjectList,
  loader: ({ context }) => context.queryClient.ensureQueryData(projectsQuery()),
})
