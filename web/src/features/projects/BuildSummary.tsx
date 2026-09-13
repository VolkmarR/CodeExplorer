import type { IndexSummary } from '@/lib/api'
import { formatCount } from '@/lib/format'

/**
 * What a build just produced. One bad repository does not fail the project, so what was left out is
 * listed with the reason rather than being silently missing from the count.
 */
export function BuildSummary({ summary }: { summary: IndexSummary }) {
  return (
    <div className="rounded-lg border bg-card px-4 py-3 text-sm">
      <p>
        Indexed {formatCount(summary.files)} files and {formatCount(summary.lines)} lines from{' '}
        {summary.repositories} {summary.repositories === 1 ? 'repository' : 'repositories'}.
      </p>
      {summary.skipped.length > 0 ? (
        <ul className="mt-2 list-disc space-y-1 pl-5 text-muted-foreground">
          {summary.skipped.map((reason) => (
            <li key={reason}>{reason}</li>
          ))}
        </ul>
      ) : null}
    </div>
  )
}
