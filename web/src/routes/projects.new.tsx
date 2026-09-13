import { createFileRoute } from '@tanstack/react-router'
import { NewProjectPage } from '@/features/projects/NewProjectPage'
import { RouteError } from '@/components/RouteError'

// A static segment beats `$project`, so a project whose slug is literally "new" would have this page
// at its URL instead of its own. The server accepts that slug, so the collision is real but cheap:
// the project is still reachable everywhere else, and no name is burned server-side.
export const Route = createFileRoute('/projects/new')({
  component: NewProjectPage,
  errorComponent: RouteError,
})
