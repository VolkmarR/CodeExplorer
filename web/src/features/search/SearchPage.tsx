import { useParams, useSearch } from '@tanstack/react-router'
import { SearchForm } from '@/features/search/SearchForm'
import { SearchResults } from '@/features/search/SearchResults'

/**
 * Search over one project's index. The query and its options come from the URL and nowhere else, so
 * the page has no state of its own and a link reproduces exactly what the sender saw.
 */
export function SearchPage() {
  const { project } = useParams({ from: '/projects/$project/search' })
  const search = useSearch({ from: '/projects/$project/search' })

  return (
    <div className="space-y-6">
      {/* The header bar already names the project and lights this view; a heading here would say it
          a second time. */}
      <SearchForm project={project} search={search} />

      {/* The form already says how a query is read, next to the box; nothing to add here. */}
      {search.q === '' ? null : <SearchResults project={project} search={search} />}
    </div>
  )
}
