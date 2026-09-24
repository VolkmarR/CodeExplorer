import { Link } from '@tanstack/react-router'
import { formatCount } from '@/lib/format'

/**
 * How many files the project's excluded paths kept out of one section (#216). Said on every section
 * that left something out, because a short ranking with nothing to say why reads as a quiet project
 * rather than a filtered one — the two mean opposite things (CODING_STANDARDS, Errors). Nothing is
 * drawn where nothing was left out, which includes a view with the excluded paths shown.
 */
export function ExcludedNote({
  project,
  files,
  what,
}: {
  project: string
  files: number | undefined
  /** What the files were left out of, e.g. "the files at HEAD". */
  what: string
}) {
  if (!files) return null
  return (
    <p className="pt-2 text-xs text-muted-foreground">
      {formatCount(files)} {files === 1 ? 'file' : 'files'} of {what} left out by the{' '}
      <Link
        to="/projects/$project/settings"
        params={{ project }}
        className="hover:text-primary hover:underline"
      >
        excluded paths
      </Link>
      .
    </p>
  )
}
