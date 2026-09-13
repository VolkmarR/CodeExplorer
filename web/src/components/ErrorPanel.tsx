import { ApiError } from '@/lib/api'

/**
 * Whatever went wrong, said in the server's own words where there are any. The API answers a
 * semantic failure with prose that names what to try instead, and rewording it here would lose that.
 */
export function ErrorPanel({ error }: { error: unknown }) {
  const message = error instanceof Error ? error.message : String(error)
  const status = error instanceof ApiError ? error.response.status : null
  return (
    <div className="rounded-lg border border-destructive/40 bg-destructive/10 px-4 py-3">
      <p className="text-sm font-medium text-destructive">
        {status ? `Request failed (${status})` : 'Something broke'}
      </p>
      <p className="mt-1 text-sm whitespace-pre-wrap text-foreground/90">{message}</p>
    </div>
  )
}
