import { createFileRoute } from '@tanstack/react-router'
import { BrowseUnavailable } from '@/features/files/BrowseUnavailable'
import { BrowsePage } from '@/features/files/BrowsePage'
import { validateBrowseSearch } from '@/features/files/browseParams'
import { browseQuery, treeQuery } from '@/features/files/queries'

export const Route = createFileRoute('/projects/$project/files')({
  component: BrowsePage,
  errorComponent: BrowseUnavailable,
  loaderDeps: ({ search }) => search,
  // Only the view the URL asks for is loaded; the other would be a second query for a component that
  // is not going to render.
  // The call is branched rather than the argument: the two queries have different key shapes, and a
  // ternary inside `ensureQueryData` would have to unify them into one that fits neither.
  loader: ({ context, deps, params }) =>
    deps.glob === ''
      ? context.queryClient.ensureQueryData(treeQuery(params.project, deps.path))
      : context.queryClient.ensureQueryData(browseQuery(params.project, deps)),
  validateSearch: validateBrowseSearch,
})
