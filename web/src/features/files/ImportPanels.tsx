import { useQuery } from '@tanstack/react-query'
import { dependentsNote, IMPORT_EVIDENCE, importsNote } from '@/features/files/imports'
import { dependentsQuery, importsQuery } from '@/features/files/queries'
import { RailEntries, RailPanelAnswer, RailWaiting, tally } from '@/features/files/RailPanel'
import { FilePathLink } from '@/components/FilePathLink'
import { directoryOf, fileName } from '@/lib/format'

/**
 * Where the file sits in the project: what it imports, and what imports it. The second is the walk a
 * codebase cannot be read for — it is every import line in the project, resolved at index time —
 * and it is why these two panels ship together rather than the first one alone.
 *
 * A name that resolved is a link to that file's page; one that did not is the name as written, with
 * the server's reason beside it, because an edge shown only when it resolves would tell the reader
 * this file depends on less than it does (CONTEXT.md, _Import_).
 */
export function ImportPanels({ project, path }: { project: string; path: string }) {
  return (
    <>
      <Imports project={project} path={path} />
      <Dependents project={project} path={path} />
    </>
  )
}

function Imports({ project, path }: { project: string; path: string }) {
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
            {edge.targetPath === null ? (
              <span className="truncate">{edge.name}</span>
            ) : (
              // The target file, not the line: the line number is where the import was written in
              // this file, and following it into the other one would land on an unrelated line.
              <FilePathLink
                project={project}
                qualifiedPath={edge.targetPath}
                atHead
                label={edge.name}
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

function Dependents({ project, path }: { project: string; path: string }) {
  const { data, error, isPending } = useQuery(dependentsQuery(project, path))

  if (isPending || error) return <RailWaiting title="Dependents" error={error} />

  return (
    <RailPanelAnswer
      title="Dependents"
      count={tally(data.dependents.length, data.capped)}
      note={dependentsNote(data)}
      evidence={IMPORT_EVIDENCE}
    >
      <RailEntries items={data.dependents} capped={data.capped}>
        {(dependent) => (
          <li
            key={`${dependent.qualifiedPath}:${dependent.lineNumber}`}
            className="min-w-0 text-xs"
            title={dependent.qualifiedPath}
          >
            {/* Straight to the import line: what a reader opens a dependent for is the line that
                names this file, not the top of that file. */}
            <FilePathLink
              project={project}
              qualifiedPath={dependent.qualifiedPath}
              atHead
              line={dependent.lineNumber}
              label={`${fileName(dependent.qualifiedPath)}:${dependent.lineNumber}`}
            />
            <p className="truncate text-muted-foreground">
              {directoryOf(dependent.qualifiedPath)}
              {' · '}
              {dependent.name}
            </p>
          </li>
        )}
      </RailEntries>
    </RailPanelAnswer>
  )
}
