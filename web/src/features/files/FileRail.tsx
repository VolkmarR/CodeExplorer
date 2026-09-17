import { useQuery } from '@tanstack/react-query'
import { Link } from '@tanstack/react-router'
import { blameQuery } from '@/features/files/queries'
import { searchSearch } from '@/features/search/searchParams'
import { CommitLine } from '@/components/CommitLine'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'
import { Skeleton } from '@/components/ui/skeleton'
import type { CommitRef, FileContent } from '@/lib/api'
import { fileName } from '@/lib/format'

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
 * the panels of #54 and #56 had nowhere to go — a single-column file page has no room for them.
 * Two of the three panels say so plainly rather than being left out: an operator comparing this
 * page with what an agent is told should be able to see which questions this server cannot answer
 * yet, and where the answers will appear.
 */
export function FileRail({ project, file }: { project: string; file: FileContent }) {
  const symbol = symbolName(file.qualifiedPath)

  return (
    <aside className="flex w-full shrink-0 flex-col gap-4 xl:w-80">
      <Panel title="Declarations">
        <p className="text-sm text-muted-foreground">
          What this file declares, read from its lines. It arrives with `find_definition` (#54),
          which is what teaches the index to answer it.
        </p>
      </Panel>

      <Panel title="References">
        <p className="text-sm text-muted-foreground">
          What names this file elsewhere in the project. Until #54, the nearest answer is a search:{' '}
          <Link
            to="/projects/$project/search"
            params={{ project }}
            search={searchSearch(symbol)}
            className="text-primary hover:underline"
          >
            search for {symbol}
          </Link>
          .
        </p>
      </Panel>

      <RecentCommits project={project} file={file} />
    </aside>
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
      <Panel title="Recent commits">
        {/* Not "nobody changed it": history arrives with a refresh and may not reach the beginning
            of the repository (CONTEXT.md), and the two mean opposite things. */}
        <p className="text-sm text-muted-foreground">
          No history is imported for this repository, so nothing can be said about what changed this
          file.
        </p>
      </Panel>
    )
  }

  return (
    <Panel title="Recent commits">
      {blame.isPending ? (
        <div className="space-y-2">
          <Skeleton className="h-4 w-full" />
          <Skeleton className="h-4 w-2/3" />
        </div>
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
    </Panel>
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

function Panel({ title, children }: { title: string; children: React.ReactNode }) {
  return (
    <Card size="sm">
      <CardHeader>
        <CardTitle>{title}</CardTitle>
      </CardHeader>
      <CardContent>{children}</CardContent>
    </Card>
  )
}

/**
 * The file's name without its extension, which for these codebases is what a type in it is usually
 * called — `SqlSelectBase.prg` declares `SqlSelectBase`. A guess and not a fact, which is why it
 * only ever seeds a search the reader can see and edit, and never claims to be a symbol.
 */
function symbolName(qualifiedPath: string) {
  const name = fileName(qualifiedPath)
  const dot = name.lastIndexOf('.')
  return dot > 0 ? name.slice(0, dot) : name
}
