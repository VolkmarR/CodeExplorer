import { useParams, useSearch } from '@tanstack/react-router'
import { PageCard } from '@/components/PageCard'
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
    <PageCard title="Search" hint="answered from the index, never from a working copy">
      <div className="space-y-5">
        <SearchForm project={project} search={search} />
        {search.q === '' ? (
          <p className="py-8 text-center text-sm text-muted-foreground">
            Type a query. Text matches whole identifiers; the regex switch reads it as a pattern.
          </p>
        ) : (
          <SearchResults project={project} search={search} />
        )}
      </div>
    </PageCard>
  )
}
