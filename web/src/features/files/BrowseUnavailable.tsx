import { Link, useParams } from '@tanstack/react-router'
import { ApiError } from '@/lib/api'
import { RouteError } from '@/components/RouteError'
import { Button } from '@/components/ui/button'

/**
 * A project whose index was never built answers the browse endpoint with a 404, and that is now the
 * first thing an operator sees after creating one — the project list leads here, not to the settings
 * page. So it reads as the ordinary starting state it is, with the way out of it, rather than as a
 * failed request. Any other failure is a real one and falls through to the usual panel.
 */
export function BrowseUnavailable({ error }: { error: unknown }) {
  const { project } = useParams({ from: '/projects/$project/files' })
  const status = error instanceof ApiError ? error.response.status : null

  if (status !== 404) return <RouteError error={error} />

  return (
    <div className="space-y-6">
      <h1 className="text-2xl font-semibold tracking-tight">
        Files <span className="font-mono text-lg font-normal text-muted-foreground">{project}</span>
      </h1>
      <div className="rounded-lg border bg-card px-4 py-6">
        <p className="text-sm font-medium">Nothing to browse yet</p>
        <p className="mt-1 text-sm text-muted-foreground">
          {/* The server's own prose covers both causes — never built, or a rebuild in flight. */}
          {error instanceof Error ? error.message : String(error)}
        </p>
        <Button
          render={<Link to="/projects/$project" params={{ project }} />}
          variant="outline"
          className="mt-4"
        >
          Project settings
        </Button>
      </div>
    </div>
  )
}
