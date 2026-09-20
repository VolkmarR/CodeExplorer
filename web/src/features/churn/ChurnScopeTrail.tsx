import { Link } from '@tanstack/react-router'
import { ChevronRight } from 'lucide-react'
import { churnSearch, type ChurnParameters } from '@/lib/urls/churnParams'

/**
 * How far into the project a rolled-up ranking has been drilled, every level a way back out.
 *
 * It exists because drilling in is one click and climbing out was none (#161): a reader three
 * directories deep could only reach the whole project again through the sidebar, which drops the
 * window and the extensions they had set. Every link here keeps them — it is the same ranking asked
 * of a shallower scope.
 *
 * Not the file view's `PathBreadcrumb`: that one links into the tree, and a segment here has to lead
 * to the churn of that directory instead. Same shape, different destination, and a feature may not
 * reach into another's components anyway.
 *
 * Nothing at all until a directory is chosen: at the project root the trail would be one link to the
 * page already open, which is a control that does nothing sitting above every unscoped ranking.
 */
export function ChurnScopeTrail({
  project,
  search,
}: {
  project: string
  search: ChurnParameters
}) {
  if (search.directory === undefined) return null

  const segments = search.directory.split('/')

  return (
    <nav className="flex flex-wrap items-center gap-1 text-sm" aria-label="Ranking scope">
      <Link
        to="/projects/$project/churn"
        params={{ project }}
        // The whole project: the directory cleared, and the repository with it, since a directory
        // carries one and clearing only half of the scope would leave the reader inside a repository
        // they never chose.
        search={churnSearch(search, { directory: '', repository: '' })}
        className="text-muted-foreground hover:text-foreground hover:underline"
      >
        {project}
      </Link>
      {segments.map((segment, index) => {
        const path = segments.slice(0, index + 1).join('/')
        return (
          <span key={path} className="flex items-center gap-1">
            <ChevronRight className="size-3.5 text-muted-foreground/60" />
            {index === segments.length - 1 ? (
              <span className="font-mono font-medium">{segment}</span>
            ) : (
              <Link
                to="/projects/$project/churn"
                params={{ project }}
                search={churnSearch(search, { directory: path })}
                className="font-mono text-muted-foreground hover:text-foreground hover:underline"
              >
                {segment}
              </Link>
            )}
          </span>
        )
      })}
    </nav>
  )
}
