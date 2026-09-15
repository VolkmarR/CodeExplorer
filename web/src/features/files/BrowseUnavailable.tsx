import { Link, useParams } from '@tanstack/react-router'
import { treeSearch } from '@/features/files/browseParams'
import { ApiError } from '@/lib/api'
import { RouteError } from '@/components/RouteError'
import { Button } from '@/components/ui/button'

/**
 * Two answers from the browse endpoint are states rather than failures, and both arrive with the
 * server's own prose saying what to do. A project whose index was never built is a 404, and that is
 * the first thing an operator sees after creating one — the project list leads here, not to the
 * settings page — so it reads as the ordinary starting state it is, with the way out of it. A link
 * whose repository slug names nothing any more is a 400 that lists what exists, and its way out is the
 * project's root. Any other failure is a real one and falls through to the usual panel.
 */
export function BrowseUnavailable({ error }: { error: unknown }) {
  const { project } = useParams({ from: '/projects/$project/files' })
  const status = error instanceof ApiError ? error.response.status : null

  if (status !== 404 && status !== 400) return <RouteError error={error} />

  return (
    <div className="space-y-6">
      <h1 className="text-2xl font-semibold tracking-tight">
        Files <span className="font-mono text-lg font-normal text-muted-foreground">{project}</span>
      </h1>
      <div className="rounded-lg border bg-card px-4 py-6">
        <p className="text-sm font-medium">
          {status === 404 ? 'Nothing to browse yet' : 'Nothing here'}
        </p>
        <p className="mt-1 text-sm text-muted-foreground">
          {/* The server's prose names the cause: never built, a rebuild in flight, or which repositories exist. */}
          {error instanceof Error ? error.message : String(error)}
        </p>
        {status === 404 ? (
          <Button
            render={<Link to="/projects/$project" params={{ project }} />}
            variant="outline"
            className="mt-4"
          >
            Project settings
          </Button>
        ) : (
          <Button
            render={
              <Link to="/projects/$project/files" params={{ project }} search={treeSearch()} />
            }
            variant="outline"
            className="mt-4"
          >
            Project root
          </Button>
        )}
      </div>
    </div>
  )
}
