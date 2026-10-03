import { Link, type LinkProps } from '@tanstack/react-router'
import { ChevronRight } from 'lucide-react'

/**
 * Where in a project the reader stands, as a qualified path with every segment a link back out to
 * that level. The tree and the file view use it to link into the tree, and the churn ranking to the
 * churn of a shallower directory: the same trail with a different destination, so `linkTo` names the
 * destination and the trail is drawn once.
 *
 * `linkTo('')` is the project itself, the first link. The last segment is the current place and is
 * text: in the file view it is the file, whose link would go to the tree at a path that is a file.
 */
export function PathTrail({
  project,
  path,
  label,
  linkTo,
}: {
  project: string
  path: string
  /** The trail's accessible name, which says what kind of place it locates. */
  label: string
  linkTo: (path: string) => LinkProps
}) {
  const segments = path === '' ? [] : path.split('/')

  return (
    <nav className="flex flex-wrap items-center gap-1 text-sm" aria-label={label}>
      <Link {...linkTo('')} className="text-muted-foreground hover:text-foreground hover:underline">
        {project}
      </Link>
      {segments.map((segment, index) => {
        const prefix = segments.slice(0, index + 1).join('/')
        return (
          <span key={prefix} className="flex items-center gap-1">
            <ChevronRight className="size-3.5 text-muted-foreground/60" />
            {index === segments.length - 1 ? (
              <span className="font-mono font-medium">{segment}</span>
            ) : (
              <Link
                {...linkTo(prefix)}
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
