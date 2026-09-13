import { createFileRoute } from '@tanstack/react-router'
import { RouteError } from '@/components/RouteError'
import { BrowsePage } from '@/features/files/BrowsePage'
import { validateBrowseSearch } from '@/features/files/browseParams'
import { browseQuery } from '@/features/files/queries'

export const Route = createFileRoute('/projects/$project/files')({
  component: BrowsePage,
  errorComponent: RouteError,
  loaderDeps: ({ search }) => search,
  loader: ({ context, deps, params }) =>
    context.queryClient.ensureQueryData(browseQuery(params.project, deps)),
  validateSearch: validateBrowseSearch,
})
