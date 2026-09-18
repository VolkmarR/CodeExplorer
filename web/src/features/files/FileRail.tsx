import { useQuery } from '@tanstack/react-query'
import { Link } from '@tanstack/react-router'
import { DeclarationPanel } from '@/features/files/DeclarationPanel'
import { ImportPanels } from '@/features/files/ImportPanels'
import { blameQuery } from '@/features/files/queries'
import { RailPanel, RailPending } from '@/features/files/RailPanel'
import { searchSearch } from '@/features/search/searchParams'
import { CommitLine } from '@/components/CommitLine'
import type { CommitRef, FileContent } from '@/lib/api'
import { splitFileName } from '@/lib/format'

/**
 * How many commits the rail names. Enough to recognise the stretch of work a file is in the middle
 * of, short enough that the rail stays a rail: past about six the reader is reading history, and
 * History is a view of its own.
 */
const RECENT_COMMITS = 6

/**
 * What else is known about the file beside it: what it declares, what refers to it, and what has
 * been done to it lately.
 *
 * It exists because the page answered less than the MCP tools over the same index do, and because
 * the import and declaration panels had nowhere to go — a single-column file page has no room for
 * them. The one question still unanswered says so in its own words rather than being left out: an
 * operator comparing this page with what an agent is told should be able to see which questions
 * this server cannot answer yet.
 */
export function FileRail({ project, file }: { project: string; file: FileContent }) {
  return (
    // The rail holds its own scroll beside the code rather than scrolling with the page, bounded by
    // the same token the code pane is: the two columns are read together, and a rail that ran past
    // the pane would leave the taller column blank for the rest of a long file. Only from `xl`,
    // where the two are side by side — stacked under the code on a narrow screen it is part of the
    // page and scrolls with it.
    //
    // Stacked with `space-y` and not as a flex column, which is what it was: a bounded flex column
    // shrinks its children to fit instead of overflowing, so every panel was crushed — a file with
    // 76 declarations showed six of them in a box with no scrollbar and no way to reach the rest.
    // `pr` for the scrollbar's own width, so a panel's text is not underneath it.
    <aside className="w-full shrink-0 space-y-4 xl:max-h-(--reading-pane) xl:w-80 xl:overflow-y-auto xl:pr-1">
      <DeclarationPanel project={project} path={file.qualifiedPath} />

      <References project={project} path={file.qualifiedPath} />

      <ImportPanels project={project} path={file.qualifiedPath} />

      <RecentCommits project={project} file={file} />
    </aside>
  )
}

/**
 * Who names this file elsewhere, which the index cannot answer of a file. It answers references of a
 * symbol, and turning that into an answer about a file would mean asking it of every name the file
 * declares — a scan of the project per file opened, for a list that would still be evidence about
 * names rather than about the file.
 *
 * So the gap is said rather than drawn as an empty panel, the way a project with no history says it
 * holds none instead of showing an empty ranking: a panel that looks like an answer and is not is
 * worse than a sentence. The search is the nearest thing there is and is offered as one — seeded
 * with the file's own name, which for these codebases is usually what the type in it is called, and
 * which the reader can see and edit because it is a guess.
 */
function References({ project, path }: { project: string; path: string }) {
  const symbol = symbolName(path)

  return (
    <RailPanel title="References">
      <p className="text-sm text-muted-foreground">
        The index answers where a symbol is referenced, not where a file is, so there is no list to
        draw here. The nearest answer is a search for what this file is likely to be called:{' '}
        <Link
          to="/projects/$project/search"
          params={{ project }}
          search={searchSearch(symbol)}
          className="text-primary hover:underline"
        >
          search for {symbol}
        </Link>
        . The Dependents panel below is the answer for the files that import this one.
      </p>
    </RailPanel>
  )
}

/**
 * The commits that last touched this file, read out of its attribution: the runs already say which
 * commit wrote each stretch, so the set of them is the file's recent history without a second
 * endpoint to ask. Ordered by when they were authored and not by where they sit in the file.
 */
function RecentCommits({ project, file }: { project: string; file: FileContent }) {
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
              <CommitLine commit={commit} subject={false} className="text-muted-foreground" />
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

/**
 * The file's name without its extension, which for these codebases is what a type in it is usually
 * called — `SqlSelectBase.prg` declares `SqlSelectBase`. A guess and not a fact, which is why it
 * only ever seeds a search the reader can see and edit, and never claims to be a symbol.
 */
function symbolName(qualifiedPath: string) {
  return splitFileName(qualifiedPath).stem
}
