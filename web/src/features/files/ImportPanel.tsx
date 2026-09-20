import { useQuery } from '@tanstack/react-query'
import { IMPORT_EVIDENCE, importsNote } from '@/features/files/imports'
import { importsQuery } from '@/features/files/queries'
import { RailEntries } from '@/features/files/RailEntries'
import { RailPanelAnswer } from '@/features/files/RailPanelAnswer'
import { RailWaiting } from '@/features/files/RailWaiting'
import { tally } from '@/features/files/tally'
import { FilePathLink } from '@/components/FilePathLink'

/**
 * What the file imports: the names it wrote, and the file each of them turned out to be.
 *
 * A name that resolved is a link to that file's page; one that did not is the name as written, with
 * the server's reason beside it, because an edge shown only when it resolves would tell the reader
 * this file depends on less than it does (CONTEXT.md, _Import_).
 *
 * A Dependents panel drew the reverse direction beside this one and is gone (#160). It could only
 * answer where a name resolved to exactly one file, and on the codebases this server is pointed at
 * that almost never held — X# writes no import line, a C# namespace spread over sibling files
 * resolved to none of them, an aliased TypeScript specifier resolved to nothing. It spent a panel
 * and a request per file opened on a list that was empty and a note explaining why, which is a box
 * that teaches a reader to skip it.
 */
export function ImportPanel({ project, path }: { project: string; path: string }) {
  const { data, error, isPending } = useQuery(importsQuery(project, path))

  if (isPending || error) return <RailWaiting title="Imports" error={error} />

  return (
    <RailPanelAnswer
      title="Imports"
      // No number where there is no list to count: an extension no profile covers and a language
      // with no imports both answer in prose, and a `0` beside it would read as a measurement.
      count={data.profiled && data.hasImports ? tally(data.imports.length, data.capped) : undefined}
      note={importsNote(data)}
      evidence={IMPORT_EVIDENCE}
    >
      {/* In the order the file wrote them, resolved and unresolved together. Sorting the resolved
          ones first would push the unresolved ones out of the collapsed panel, which is the reading
          this panel exists to prevent: a dependency left off the screen is one the file looks not to
          have. */}
      <RailEntries items={data.imports} capped={data.capped}>
        {(edge) => (
          <li
            key={`${edge.lineNumber}:${edge.name}`}
            className="min-w-0 text-xs"
            title={edge.targetPath ?? edge.unresolved ?? edge.name}
          >
            {/* Wrapped and not cut off: an import name is a dotted namespace or a path, and what
                tells two of them apart is at the END. Truncated, `…Infrastructure.McSerializer` and
                `…Infrastructure.McReader` are the same row drawn twice, which is what the panel
                looked like on a real project (#160). The reason beneath it still truncates — it is
                one of a handful of fixed sentences, and its first words say which. */}
            {edge.targetPath === null ? (
              <span className="wrap-anywhere">{edge.name}</span>
            ) : (
              // The target file, not the line: the line number is where the import was written in
              // this file, and following it into the other one would land on an unrelated line.
              <FilePathLink
                project={project}
                qualifiedPath={edge.targetPath}
                atHead
                label={edge.name}
                wrap
              />
            )}
            <p className="truncate text-muted-foreground">
              {/* Said in a word and not only in a colour: the row below a resolved name is a path,
                  and without the label the two read as the same kind of answer. */}
              {edge.targetPath ?? `unresolved · ${edge.unresolved}`}
            </p>
          </li>
        )}
      </RailEntries>
    </RailPanelAnswer>
  )
}
