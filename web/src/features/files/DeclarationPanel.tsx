import { useQuery } from '@tanstack/react-query'
import { Link } from '@tanstack/react-router'
import {
  DECLARATION_EVIDENCE,
  declarationLabel,
  declarationsNote,
} from '@/features/files/declarations'
import { fileSearch } from '@/features/files/fileParams'
import { declarationsQuery } from '@/features/files/queries'
import { RailEntries, RailPanel, RailPending } from '@/features/files/RailPanel'
import { ErrorPanel } from '@/components/ErrorPanel'
import { formatCount } from '@/lib/format'

/**
 * What the file declares, read from the index rather than from the file on screen. It is the same
 * answer `find_definition` gives an agent, asked of a path instead of a symbol, so the page and the
 * tools cannot disagree about what is in a file.
 *
 * Each entry is a link to its own line, which is what makes the panel a table of contents for a long
 * file: the one thing a reader of a 4900-line generated file wants is to land on the routine rather
 * than to scroll for it.
 */
export function DeclarationPanel({ project, path }: { project: string; path: string }) {
  const { data, error, isPending } = useQuery(declarationsQuery(project, path))

  if (isPending || error) {
    return (
      <RailPanel title="Declarations">
        {error ? (
          <ErrorPanel error={error} title="The declarations could not be read" />
        ) : (
          <RailPending />
        )}
      </RailPanel>
    )
  }

  const note = declarationsNote(data)

  return (
    <RailPanel
      title="Declarations"
      // No number where there is no list to count: an extension no profile covers and a language
      // whose declarations cannot be read both answer in prose, and a `0` beside either would read
      // as a measurement.
      count={
        data.readsDeclarations && data.declarations.length > 0
          ? `${formatCount(data.declarations.length)}${data.capped ? '+' : ''}`
          : undefined
      }
    >
      {note ? <p className="mb-3 text-sm text-muted-foreground last:mb-0">{note}</p> : null}
      {/* A screenful, with the rest a click away, like the import panels below it: a generated file
          declaring seventy names would otherwise fill the whole rail and push every other panel out
          of it. */}
      <RailEntries items={data.declarations} capped={data.capped}>
        {(declaration) => (
          <li
            key={`${declaration.lineNumber}:${declarationLabel(declaration)}`}
            className="min-w-0 text-xs"
            title={declaration.text.trim()}
          >
            <Link
              to="/projects/$project/file"
              params={{ project }}
              search={fileSearch(path, declaration.lineNumber)}
              replace
              className="block truncate text-primary hover:underline"
            >
              {declarationLabel(declaration)}
            </Link>
            <p className="truncate text-muted-foreground">
              line {declaration.lineNumber}
              {/* Only where the language draws the split. Where it does not, saying nothing is the
                  answer rather than picking one of the two labels (CONTEXT.md, Declaration). */}
              {declaration.role === null ? null : ` · ${declaration.role}`}
            </p>
          </li>
        )}
      </RailEntries>
      {data.readsDeclarations ? (
        <p className="mt-3 text-xs leading-snug text-muted-foreground/80">{DECLARATION_EVIDENCE}</p>
      ) : null}
    </RailPanel>
  )
}
