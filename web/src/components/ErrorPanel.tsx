import { ApiError } from '@/lib/http'

/**
 * Whatever went wrong, said in the server's own words where there are any. The API answers a
 * semantic failure with prose that names what to try instead, and rewording it here would lose that.
 */
export function ErrorPanel({ error, title }: { error: unknown; title?: string }) {
  const message = error instanceof Error ? error.message : String(error)
  const status = error instanceof ApiError ? error.response.status : null
  return (
    <div className="rounded-lg border border-destructive/40 bg-destructive/10 px-4 py-3">
      {/* A caller that knows what failed says so; otherwise the status line is all there is to go on.
          The panel is one component so the two never diverge on the same page. */}
      <p className="text-sm font-medium text-destructive">
        {title ?? (status ? `Request failed (${status})` : 'Something broke')}
      </p>
      <p className="mt-1 text-sm whitespace-pre-wrap text-foreground/90">{message}</p>
    </div>
  )
}
