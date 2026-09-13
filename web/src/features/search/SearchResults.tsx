import { useSuspenseQuery } from '@tanstack/react-query'
import { Link, useNavigate } from '@tanstack/react-router'
import { searchQuery } from '@/features/search/queries'
import type { SearchParameters } from '@/features/search/searchParams'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { formatCount } from '@/lib/format'

/**
 * A page of matches, each file linking through to its own content by qualified path — the repository
 * slug and then the path inside it, which is what tells two repositories' `src/index.ts` apart.
 */
export function SearchResults({ project, search }: { project: string; search: SearchParameters }) {
  const navigate = useNavigate()
  const { data: result } = useSuspenseQuery(searchQuery(project, search))
  const lastPage = Math.max(1, Math.ceil(result.totalFiles / result.pageSize))

  if (result.totalFiles === 0) {
    return (
      <p className="text-sm text-muted-foreground">
        {/* "Nothing matched" and "matches existed and the filters hid them" read the same and mean
            opposite things, so the server counts the unfiltered matches and the page says which. */}
        {result.filesMatchingWithoutFilters
          ? `No matches under these filters. Without them, ${formatCount(result.filesMatchingWithoutFilters)} files match.`
          : 'No matches.'}
      </p>
    )
  }

  return (
    <div className="space-y-4">
      <p className="flex items-center gap-2 text-sm text-muted-foreground">
        <Badge variant="secondary">{result.engine}</Badge>
        {formatCount(result.totalFiles)} files, {formatCount(result.totalLines)} lines
      </p>

      <ul className="space-y-4">
        {result.files.map((file) => (
          <li key={file.qualifiedPath} className="overflow-hidden rounded-lg border bg-card">
            <div className="flex items-baseline justify-between gap-4 border-b px-4 py-2">
              <Link
                to="/projects/$project/file"
                params={{ project }}
                search={{ path: file.qualifiedPath }}
                className="font-mono text-sm hover:underline"
              >
                {file.qualifiedPath}
              </Link>
              <span className="shrink-0 text-xs text-muted-foreground">
                {formatCount(file.matchCount)} {file.matchCount === 1 ? 'match' : 'matches'}
                {file.matchesShown < file.matchCount ? `, showing ${file.matchesShown}` : ''}
              </span>
            </div>
            <table className="w-full border-collapse font-mono text-xs">
              <tbody>
                {file.lines.map((line) => (
                  <tr key={line.lineNumber} className={line.isMatch ? 'bg-primary/10' : undefined}>
                    <td className="w-14 shrink-0 border-r px-2 py-0.5 text-right text-muted-foreground select-none">
                      <Link
                        to="/projects/$project/file"
                        params={{ project }}
                        search={{ line: line.lineNumber, path: file.qualifiedPath }}
                        className="hover:underline"
                      >
                        {line.lineNumber}
                      </Link>
                    </td>
                    <td className="overflow-x-auto px-3 py-0.5 whitespace-pre">{line.text}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </li>
        ))}
      </ul>

      {lastPage > 1 ? (
        <div className="flex items-center justify-center gap-4">
          <Button
            variant="outline"
            size="sm"
            disabled={search.page <= 1}
            onClick={() =>
              void navigate({
                params: { project },
                search: { ...search, page: search.page - 1 },
                to: '/projects/$project/search',
              })
            }
          >
            Previous
          </Button>
          <span className="text-sm text-muted-foreground">
            Page {search.page} of {lastPage}
          </span>
          <Button
            variant="outline"
            size="sm"
            disabled={search.page >= lastPage}
            onClick={() =>
              void navigate({
                params: { project },
                search: { ...search, page: search.page + 1 },
                to: '/projects/$project/search',
              })
            }
          >
            Next
          </Button>
        </div>
      ) : null}
    </div>
  )
}
