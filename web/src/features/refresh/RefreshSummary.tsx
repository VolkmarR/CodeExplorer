import type { IndexSummary } from '@/features/refresh/api'
import { formatCount } from '@/lib/format'

/**
 * What a refresh produced. One bad repository does not fail the project, so what was left out is
 * listed with the reason rather than being silently missing from the count, and a choice the
 * refresh made for the operator — the branch a detached remote is followed on — is listed beside it.
 */
export function RefreshSummary({ summary }: { summary: IndexSummary }) {
  const lines = [...summary.skipped, ...summary.notes]
  return (
    <div className="rounded-lg border bg-card px-4 py-3 text-sm">
      <p>
        Indexed {formatCount(summary.files)} files and {formatCount(summary.lines)} lines from{' '}
        {summary.repositories} {summary.repositories === 1 ? 'repository' : 'repositories'}.
      </p>
      {lines.length > 0 ? (
        <ul className="mt-2 list-disc space-y-1 pl-5 text-muted-foreground">
          {lines.map((line) => (
            <li key={line}>{line}</li>
          ))}
        </ul>
      ) : null}
    </div>
  )
}
