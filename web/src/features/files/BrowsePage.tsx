import { Link, useParams, useSearch } from '@tanstack/react-router'
import { BrowseFilter } from '@/features/files/BrowseFilter'
import { FileList } from '@/features/files/FileList'
import { Button } from '@/components/ui/button'

/**
 * Browsing a project's index: what is in there, rather than what matches a query. The pair with
 * search, and the answer to "I don't know what to search for yet".
 */
export function BrowsePage() {
  const { project } = useParams({ from: '/projects/$project/files' })
  const search = useSearch({ from: '/projects/$project/files' })

  return (
    <div className="space-y-6">
      <div className="flex flex-wrap items-center justify-between gap-4">
        <h1 className="text-2xl font-semibold tracking-tight">
          Files{' '}
          <span className="font-mono text-lg font-normal text-muted-foreground">{project}</span>
        </h1>
        <div className="flex gap-2">
          <Button
            render={
              <Link
                to="/projects/$project/search"
                params={{ project }}
                search={{ caseSensitive: false, page: 1, q: '', regex: false }}
              />
            }
            variant="secondary"
          >
            Search
          </Button>
          <Button render={<Link to="/projects/$project" params={{ project }} />} variant="ghost">
            Back to project
          </Button>
        </div>
      </div>

      <BrowseFilter project={project} search={search} />
      <FileList project={project} search={search} />
    </div>
  )
}
