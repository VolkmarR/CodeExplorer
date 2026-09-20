import { useQuery } from '@tanstack/react-query'
import type { Origin } from '@/lib/urls/views'
import { DeclarationPanel } from '@/features/files/DeclarationPanel'
import { ImportPanels } from '@/features/files/ImportPanels'
import { blameQuery } from '@/features/files/queries'
import { RailPanel } from '@/features/files/RailPanel'
import { RailPending } from '@/features/files/RailPending'
import { CommitLine } from '@/components/CommitLine'
import { ScrollArea } from '@/components/ui/scroll-area'
import type { CommitRef, FileContent } from '@/lib/api'

/**
 * How many commits the rail names. Enough to recognise the stretch of work a file is in the middle
 * of, short enough that the rail stays a rail: past about six the reader is reading history, and
 * History is a view of its own.
 */
const RECENT_COMMITS = 6

/**
 * What else is known about the file beside it: what it declares, what imports it and what it
 * imports, and what has been done to it lately.
 *
 * It exists because the page answered less than the MCP tools over the same index do, and because
 * the import and declaration panels had nowhere to go — a single-column file page has no room for
 * them. Every panel here draws an answer; it carried one that drew a paragraph explaining that the
 * index answers references of a symbol rather than of a file, which cost a reader a heading and a
 * box to learn nothing about this file and pushed the answers that exist further down. Dependents
 * is the answer to "what refers to this file", and the distinction the paragraph made now lives in
 * CONTEXT.md under _Reference_, where the vocabulary is.
 */
export function FileRail({
  project,
  file,
  origin,
}: {
  project: string
  file: FileContent
  /**
   * How this file page was reached, carried into the links that stay in the reader's trail: a
   * declaration's own line, and a commit of this file. An import or a dependent opens a different
   * file, which the reader did not reach from this page's commit, so those start a trail of their own.
   */
  origin?: Origin
}) {
  return (
    // The rail holds its own scroll beside the code rather than scrolling with the page, bounded by
    // the same token the code pane is: the two columns are read together, and a rail that ran past
    // the pane would leave the taller column blank for the rest of a long file. Only from `xl`,
    // where the two are side by side — stacked under the code on a narrow screen it is part of the
    // page and scrolls with it, which is what the unbounded `max-h-none` below says.
    //
    // A `ScrollArea` and not the native `overflow-y-auto` it was: that gave the rail the browser's
    // own scrollbar, a bright track against the dark card column and plainly not the one the code
    // pane beside it has.
    <ScrollArea className="w-full shrink-0 max-h-none xl:max-h-(--reading-pane) xl:w-80">
      {/* The padding is inside the scroll area, so a card at any edge of the scroll shows all four
          of its borders instead of being cut flush against the clip edge — the cards are what is
          scrolled, and a box that reads as unclosed reads as broken. On every side and not only the
          two it scrolls between: a card's border is `ring-1`, which is drawn outside its box rather
          than inside it, so the left edge sat under the viewport's clip and every panel in the rail
          lost that one border while keeping the other three.
          A whole step and not the 1px a border strictly needs: a focus ring around a card at the
          edge has to fit too. `pr` is wider still, because it also keeps a panel's text out from
          under the scrollbar. */}
      <aside className="space-y-4 p-1 xl:pr-2.5">
        <DeclarationPanel project={project} path={file.qualifiedPath} origin={origin} />

        <ImportPanels project={project} path={file.qualifiedPath} />

        <RecentCommits project={project} file={file} origin={origin} />
      </aside>
    </ScrollArea>
  )
}

/**
 * The commits that last touched this file, read out of its attribution: the runs already say which
 * commit wrote each stretch, so the set of them is the file's recent history without a second
 * endpoint to ask. Ordered by when they were authored and not by where they sit in the file.
 */
function RecentCommits({
  project,
  file,
  origin,
}: {
  project: string
  file: FileContent
  origin?: Origin
}) {
  const hasHistory = file.lastCommit !== null
  const blame = useQuery({
    ...blameQuery(project, file.qualifiedPath),
    enabled: hasHistory && file.skipReason === null,
  })

  if (!hasHistory) {
    return (
      <RailPanel title="Recent commits">
        {/* Not "nobody changed it": history arrives with a refresh and may not reach the beginning
            of the repository (CONTEXT.md), and the two mean opposite things. */}
        <p className="text-sm text-muted-foreground">
          No history is imported for this repository, so nothing can be said about what changed this
          file.
        </p>
      </RailPanel>
    )
  }

  return (
    <RailPanel title="Recent commits">
      {blame.isPending ? (
        <RailPending />
      ) : (
        <ul className="space-y-2.5">
          {distinctCommits(blame.data?.runs ?? []).map((commit) => (
            <li key={commit.sha} className="min-w-0 text-xs">
              <p className="truncate">{commit.subject}</p>
              <CommitLine
                commit={commit}
                project={project}
                from={origin?.view ?? 'files'}
                subject={false}
                className="text-muted-foreground"
              />
            </li>
          ))}
        </ul>
      )}
    </RailPanel>
  )
}

/** The commits behind a file's runs, newest first, each named once however many runs it wrote. */
function distinctCommits(runs: { by: CommitRef | null }[]): CommitRef[] {
  const bySha = new Map<string, CommitRef>()
  for (const run of runs) if (run.by) bySha.set(run.by.sha, run.by)
  return [...bySha.values()]
    .toSorted((a, b) => b.authoredAt.localeCompare(a.authoredAt))
    .slice(0, RECENT_COMMITS)
}
