import { useQuery } from '@tanstack/react-query'
import { Link } from '@tanstack/react-router'
import type { Origin } from '@/lib/urls/views'
import {
  DECLARATION_EVIDENCE,
  declarationLabel,
  declarationsNote,
} from '@/features/files/declarations'
import { fileSearch } from '@/lib/urls/fileParams'
import { declarationsQuery } from '@/features/files/queries'
import { RailEntries } from '@/features/files/RailEntries'
import { RailPanelAnswer } from '@/features/files/RailPanelAnswer'
import { RailWaiting } from '@/features/files/RailWaiting'
import { tally } from '@/features/files/tally'

/**
 * What the file declares, read from the index rather than from the file on screen. It is the same
 * answer `find_definition` gives an agent, asked of a path instead of a symbol, so the page and the
 * tools cannot disagree about what is in a file.
 *
 * Each entry is a link to its own line, which is what makes the panel a table of contents for a long
 * file: the one thing a reader of a 4900-line generated file wants is to land on the routine rather
 * than to scroll for it.
 */
export function DeclarationPanel({
  project,
  path,
  origin,
}: {
  project: string
  path: string
  /** Carried through, because a declaration's link lands on this same file and must keep its trail. */
  origin?: Origin
}) {
  const { data, error, isPending } = useQuery(declarationsQuery(project, path))

  if (isPending || error) return <RailWaiting title="Declarations" error={error} />

  return (
    <RailPanelAnswer
      title="Declarations"
      // No number where there is no list to count: a `0` beside the heading would read as a
      // measurement where the answer is prose about why nothing was read.
      count={
        data.declarations.length > 0 ? tally(data.declarations.length, data.capped) : undefined
      }
      note={declarationsNote(data)}
      // Nothing was read, so there is no claim to qualify.
      evidence={data.coverage === 'unreadable' ? undefined : DECLARATION_EVIDENCE}
    >
      {/* A screenful, with the rest a click away, like the import panels below it: a generated file
          declaring seventy names would otherwise fill the whole rail and push every other panel out
          of it. */}
      <RailEntries items={data.declarations} capped={data.capped}>
        {(declaration) => {
          const label = declarationLabel(declaration)
          return (
            // The line number alone: one declaration is reported per line, so it already identifies
            // the row and is the identity the link is built from.
            <li key={declaration.lineNumber} className="min-w-0 text-xs" title={declaration.text}>
              <Link
                to="/projects/$project/file"
                params={{ project }}
                search={fileSearch(path, declaration.lineNumber, origin)}
                replace
                className="block truncate text-primary hover:underline"
              >
                {label}
              </Link>
              <p className="truncate text-muted-foreground">
                line {declaration.lineNumber}
                {/* Only where the language draws the split. Where it does not, saying nothing is the
                    answer rather than picking one of the two labels (CONTEXT.md, Declaration). */}
                {declaration.role === null ? null : ` · ${declaration.role}`}
              </p>
            </li>
          )
        }}
      </RailEntries>
    </RailPanelAnswer>
  )
}
