import { Link, useParams, useSearch } from '@tanstack/react-router'
import { SearchForm } from '@/features/search/SearchForm'
import { SearchResults } from '@/features/search/SearchResults'
import { Button } from '@/components/ui/button'

/**
 * Search over one project's index. The query and its options come from the URL and nowhere else, so
 * the page has no state of its own and a link reproduces exactly what the sender saw.
 */
export function SearchPage() {
  const { project } = useParams({ from: '/projects/$project/search' })
  const search = useSearch({ from: '/projects/$project/search' })

  return (
    <div className="space-y-6">
      <div className="flex flex-wrap items-center justify-between gap-4">
        <h1 className="text-2xl font-semibold tracking-tight">
          Search{' '}
          <span className="font-mono text-lg font-normal text-muted-foreground">{project}</span>
        </h1>
        <Button render={<Link to="/projects/$project" params={{ project }} />} variant="ghost">
          Back to project
        </Button>
      </div>

      <SearchForm project={project} search={search} />

      {search.q === '' ? (
        <p className="text-sm text-muted-foreground">
          Type a query. Text search matches whole identifiers; turn on regex for an RE2 pattern — no
          lookaround, no backreferences.
        </p>
      ) : (
        <SearchResults project={project} search={search} />
      )}
    </div>
  )
}
