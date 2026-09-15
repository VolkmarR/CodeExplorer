import { useSuspenseQuery } from '@tanstack/react-query'
import { Link, useNavigate } from '@tanstack/react-router'
import { MatchedLine } from '@/features/search/MatchedLine'
import { matchPattern, matchRanges } from '@/features/search/matchRanges'
import { searchQuery } from '@/features/search/queries'
import type { SearchParameters } from '@/features/search/searchParams'
import { languageFor } from '@/highlight/highlighter'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { formatCount } from '@/lib/format'

/** One shared empty array for context lines, so a row without marks does not get a new prop each render. */
const NO_MATCHES: never[] = []

/**
 * A page of matches, each file linking through to its own content by qualified path — the repository
 * slug and then the path inside it, which is what tells two repositories' `src/index.ts` apart.
 */
export function SearchResults({ project, search }: { project: string; search: SearchParameters }) {
  const navigate = useNavigate()
  const { data: result } = useSuspenseQuery(searchQuery(project, search))
  const lastPage = Math.max(1, Math.ceil(result.totalFiles / result.pageSize))
  const pattern = matchPattern(search)

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
        {formatCount(result.totalFiles)} files · {formatCount(result.totalLines)} lines
      </p>

      <ul className="space-y-4">
        {result.files.map((file) => {
          // The directory is dimmed and the name is not: a page of results is scanned by file name,
          // and the repository and folder are there to tell two of the same name apart.
          const cut = file.qualifiedPath.lastIndexOf('/') + 1
          const language = languageFor(file.qualifiedPath)
          return (
            <li key={file.qualifiedPath} className="overflow-hidden rounded-lg border bg-card">
              <div className="flex items-baseline justify-between gap-4 border-b px-4 py-1.5">
                <Link
                  to="/projects/$project/file"
                  params={{ project }}
                  search={{ path: file.qualifiedPath }}
                  className="font-mono text-sm hover:underline"
                >
                  <span className="text-muted-foreground">{file.qualifiedPath.slice(0, cut)}</span>
                  {file.qualifiedPath.slice(cut)}
                </Link>
                <span className="shrink-0 text-xs text-muted-foreground">
                  {formatCount(file.matchCount)} {file.matchCount === 1 ? 'match' : 'matches'}
                  {file.matchesShown < file.matchCount ? `, showing ${file.matchesShown}` : ''}
                </span>
              </div>
              <table className="w-full border-collapse font-mono text-xs">
                <tbody>
                  {file.lines.map((line) => (
                    <tr
                      key={line.lineNumber}
                      className={line.isMatch ? 'bg-primary/10' : undefined}
                    >
                      <td className="w-14 shrink-0 border-r px-2 py-0.5 text-right text-muted-foreground/70 select-none">
                        <Link
                          to="/projects/$project/file"
                          params={{ project }}
                          search={{ line: line.lineNumber, path: file.qualifiedPath }}
                          className="hover:text-primary hover:underline"
                        >
                          {line.lineNumber}
                        </Link>
                      </td>
                      <td className="overflow-x-auto px-3 py-0.5 whitespace-pre">
                        <MatchedLine
                          text={line.text}
                          language={language}
                          // Context lines carry no match by the server's word, so they are not
                          // searched again: a context line can contain the query without counting.
                          ranges={line.isMatch ? matchRanges(line.text, pattern) : NO_MATCHES}
                        />
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </li>
          )
        })}
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
