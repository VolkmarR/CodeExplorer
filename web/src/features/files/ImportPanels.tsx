import { useQuery } from '@tanstack/react-query'
import { useState } from 'react'
import {
  dependentsNote,
  ENTRIES_SHOWN,
  IMPORT_EVIDENCE,
  importsNote,
} from '@/features/files/imports'
import { dependentsQuery, importsQuery } from '@/features/files/queries'
import { RailPanel, RailPending } from '@/features/files/RailPanel'
import { ErrorPanel } from '@/components/ErrorPanel'
import { FilePathLink } from '@/components/FilePathLink'
import { Button } from '@/components/ui/button'
import { directoryOf, fileName, formatCount } from '@/lib/format'

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

  if (isPending || error) return <Waiting title="Imports" error={error} />

  return (
    <GraphPanel
      title="Imports"
      // No number where there is no list to count: an extension no profile covers and a language
      // with no imports both answer in prose, and a `0` beside it would read as a measurement.
      count={data.profiled && data.hasImports ? tally(data.imports.length, data.capped) : undefined}
      note={importsNote(data)}
    >
      {/* In the order the file wrote them, resolved and unresolved together. Sorting the resolved
          ones first would push the unresolved ones out of the collapsed panel, which is the reading
          this panel exists to prevent: a dependency left off the screen is one the file looks not to
          have. */}
      <Entries items={data.imports} capped={data.capped}>
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
      </Entries>
    </GraphPanel>
  )
}

function Dependents({ project, path }: { project: string; path: string }) {
  const { data, error, isPending } = useQuery(dependentsQuery(project, path))

  if (isPending || error) return <Waiting title="Dependents" error={error} />

  return (
    <GraphPanel
      title="Dependents"
      count={tally(data.dependents.length, data.capped)}
      note={dependentsNote(data)}
    >
      <Entries items={data.dependents} capped={data.capped}>
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
      </Entries>
    </GraphPanel>
  )
}

/**
 * One of the two panels: a heading with a count, whatever the answer has to explain about itself,
 * the list, and the caveat both of them carry. Written once because the two differ in their rows and
 * in nothing else, and a fix to the wrapper made in one of them is a fix the other would not get.
 */
function GraphPanel({
  title,
  count,
  note,
  children,
}: {
  title: string
  count?: string
  note: string | null
  children: React.ReactNode
}) {
  return (
    <RailPanel title={title} count={count}>
      {note ? <p className="mb-3 text-sm text-muted-foreground last:mb-0">{note}</p> : null}
      {children}
      <p className="mt-3 text-xs leading-snug text-muted-foreground/80">{IMPORT_EVIDENCE}</p>
    </RailPanel>
  )
}

/**
 * The list and the way out of a long one. A hub with two hundred dependents would push the rest of
 * the rail off the screen, so the panel shows a screenful and says how many it is holding back; once
 * opened it scrolls in place rather than growing without bound.
 *
 * It owns the expansion rather than taking it as a prop, because nothing outside it reads that state
 * and a panel that held it would hold it twice.
 */
function Entries<T>({
  items,
  capped,
  children,
}: {
  items: T[]
  /** Whether the length is the server's ceiling rather than the count: the button must not say "all". */
  capped: boolean
  children: (item: T) => React.ReactNode
}) {
  const [expanded, setExpanded] = useState(false)

  if (items.length === 0) return null

  return (
    <>
      <ul className={`space-y-2.5 ${expanded ? 'max-h-96 overflow-y-auto pr-1' : ''}`}>
        {(expanded ? items : items.slice(0, ENTRIES_SHOWN)).map(children)}
      </ul>
      {items.length > ENTRIES_SHOWN ? (
        <Button
          variant="ghost"
          size="sm"
          className="mt-2 -ml-2"
          onClick={() => setExpanded((open) => !open)}
        >
          {expanded
            ? 'Show fewer'
            : `Show ${capped ? 'the first' : 'all'} ${formatCount(items.length)}`}
        </Button>
      ) : null}
    </>
  )
}

/** A panel with nothing to show yet, and the same panel when the request failed outright. */
function Waiting({ title, error }: { title: string; error: unknown }) {
  return (
    <RailPanel title={title}>
      {error ? <ErrorPanel error={error} title={`${title} could not be read`} /> : <RailPending />}
    </RailPanel>
  )
}

/**
 * The number beside a heading. A list that stopped at the server's ceiling is the one case where its
 * length is not the answer to "how many", so it is shown as a floor rather than as a total.
 */
function tally(length: number, capped: boolean) {
  return capped ? `${formatCount(length)}+` : formatCount(length)
}
