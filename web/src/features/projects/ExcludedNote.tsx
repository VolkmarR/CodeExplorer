import { Link } from '@tanstack/react-router'
import { excludedLead } from '@/features/projects/excludedPaths'

/**
 * How many files the project's excluded paths kept out of one section (#216). Said on every section
 * that left something out, because a short ranking with nothing to say why reads as a quiet project
 * rather than a filtered one — the two mean opposite things (CODING_STANDARDS, Errors). Nothing is
 * drawn where nothing was left out, which includes a view with the excluded paths shown. Set in the
 * body font, as prose, under a card whose rows are mono paths too.
 */
export function ExcludedNote({
  project,
  files,
  which,
  from,
  remark,
}: {
  project: string
  files: number | undefined
  /** Which files were counted, read after the noun: "at HEAD", "changed in this window". */
  which: string
  /** The section they were left out of, where another card on the page counts the same files. */
  from?: string
  /** A sentence after the note, for a count that needs one to be read right. */
  remark?: string
}) {
  if (!files) return null
  return (
    <p className="pt-2 font-sans text-xs text-muted-foreground">
      {excludedLead(files, which, from)}{' '}
      <Link
        to="/projects/$project/settings"
        params={{ project }}
        className="hover:text-primary hover:underline"
      >
        excluded paths
      </Link>
      .{remark ? ` ${remark}` : null}
    </p>
  )
}
