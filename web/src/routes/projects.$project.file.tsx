import { createFileRoute, notFound } from '@tanstack/react-router'
import { RouteError } from '@/components/RouteError'
import { FilePage } from '@/features/files/FilePage'
import { validateFileSearch } from '@/lib/urls/fileParams'
import { fileQuery } from '@/features/files/queries'

export const Route = createFileRoute('/projects/$project/file')({
  component: FilePage,
  errorComponent: RouteError,
  // Only the path decides what to load; the line moves the viewport and must not refetch the file.
  loaderDeps: ({ search }) => ({ path: search.path }),
  // A URL with no path names no file, so this is not found. Asking the API for the empty path would
  // answer 404 as well, but as a failed request rather than as the missing page it actually is.
  loader: ({ context, deps, params }) => {
    if (deps.path === '') throw notFound()

    return context.queryClient.ensureQueryData(fileQuery(params.project, deps.path))
  },
  validateSearch: validateFileSearch,
})
