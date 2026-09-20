import { useSuspenseQuery } from '@tanstack/react-query'
import { useNavigate } from '@tanstack/react-router'
import { SearchResultGroup } from '@/features/search/SearchResultGroup'
import { matchPattern } from '@/features/search/matchRanges'
import { searchQuery } from '@/features/search/queries'
import type { SearchParameters } from '@/lib/urls/searchParams'
import { Pager } from '@/components/Pager'
import { formatCount } from '@/lib/format'

/**
 * A page of matches, each file linking through to its own content by qualified path — the repository
 * slug and then the path inside it, which is what tells two repositories' `src/index.ts` apart.
 *
 * How many matched and how long that took is not here but in the filter row above, beside the
 * filters that decided it.
 */
export function SearchResults({ project, search }: { project: string; search: SearchParameters }) {
  const navigate = useNavigate()
  const { data } = useSuspenseQuery(searchQuery(project, search))
  const result = data.result
  const lastPage = Math.max(1, Math.ceil(result.totalFiles / result.pageSize))
  const pattern = matchPattern(search)

  if (result.totalFiles === 0) {
    return (
      <p className="py-8 text-center text-sm text-muted-foreground">
        {/* "Nothing matched" and "matches existed and the filters hid them" read the same and mean
            opposite things, so the server counts the unfiltered matches and the page says which. */}
        {result.filesMatchingWithoutFilters
          ? `No matches under these filters. Without them, ${formatCount(result.filesMatchingWithoutFilters)} files match.`
          : 'No matches.'}
      </p>
    )
  }

  return (
    <div className="space-y-3">
      <ul className="space-y-3">
        {result.files.map((file) => (
          <li key={file.qualifiedPath}>
            <SearchResultGroup project={project} file={file} pattern={pattern} />
          </li>
        ))}
      </ul>

      <Pager
        page={search.page}
        lastPage={lastPage}
        onPage={(page) =>
          void navigate({
            params: { project },
            search: { ...search, page },
            to: '/projects/$project/search',
          })
        }
      />
    </div>
  )
}
