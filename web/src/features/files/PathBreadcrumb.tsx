import { Link } from '@tanstack/react-router'
import { ChevronRight } from 'lucide-react'
import { treeSearch } from '@/features/files/browseParams'

/**
 * Where in a project's tree the reader stands, every segment a link to that level. The tree and the
 * file view share it, so walking down into a file and back up reads as one path rather than as two
 * pages with two ideas of navigation.
 *
 * The last segment is the current place. In the tree that is the directory being listed; in the file
 * view it is the file, whose link would go to the tree at a path that is a file — so it is text.
 */
export function PathBreadcrumb({ project, path }: { project: string; path: string }) {
  const segments = path === '' ? [] : path.split('/')

  return (
    <nav className="flex flex-wrap items-center gap-1 text-sm" aria-label="Breadcrumb">
      <Link
        to="/projects/$project/files"
        params={{ project }}
        search={treeSearch()}
        className="text-muted-foreground hover:text-foreground hover:underline"
      >
        {project}
      </Link>
      {segments.map((segment, index) => (
        <span key={segments.slice(0, index + 1).join('/')} className="flex items-center gap-1">
          <ChevronRight className="size-3.5 text-muted-foreground/60" />
          {index === segments.length - 1 ? (
            <span className="font-mono font-medium">{segment}</span>
          ) : (
            <Link
              to="/projects/$project/files"
              params={{ project }}
              search={treeSearch(segments.slice(0, index + 1).join('/'))}
              className="font-mono text-muted-foreground hover:text-foreground hover:underline"
            >
              {segment}
            </Link>
          )}
        </span>
      ))}
    </nav>
  )
}
