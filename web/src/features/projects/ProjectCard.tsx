import { useEffect } from 'react'
import { useQueryClient, useSuspenseQuery } from '@tanstack/react-query'
import { RefreshCw } from 'lucide-react'
import { ErrorPanel } from '@/components/ErrorPanel'
import { PageCard } from '@/components/PageCard'
import { IndexStatus } from '@/features/projects/IndexStatus'
import { invalidateProject, projectQuery } from '@/features/projects/queries'
import { RefreshProgress } from '@/features/refresh/RefreshProgress'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { isRefreshRunning, refreshStatusQuery } from '@/features/refresh/queries'
import { useRefreshProject } from '@/features/refresh/useRefreshProject'

/**
 * The card both halves of a project sit in: what the project is called, what its index is, and the
 * one action worth taking on it from either page.
 *
 * It exists because the overview and the settings are two routes now and were one page before. The
 * header, the refresh button and what a running refresh reports are the same on both — a refresh is
 * the one thing happening to the project whichever half is open — so they are written here rather
 * than in each route's component, where the second copy would be the one that drifts.
 *
 * A refresh fetches every repository and rebuilds beside the live index, so the button hands the
 * work off and the page follows it by polling; searches keep answering the whole time.
 */
export function ProjectCard({
  project: slug,
  children,
}: {
  project: string
  children: React.ReactNode
}) {
  const { data: project } = useSuspenseQuery(projectQuery(slug))
  const { data: status } = useSuspenseQuery(refreshStatusQuery(slug))
  const queryClient = useQueryClient()

  const running = isRefreshRunning(status)
  const refresh = useRefreshProject(slug)

  // The index only changes when a refresh finishes, and the status is the only thing that says so.
  // Keyed on the state alone: a second refresh passes through Queued and Running on its way back to
  // Succeeded, so this fires once per refresh rather than on every poll reporting the same one.
  useEffect(() => {
    if (status.state === 'Succeeded') void invalidateProject(queryClient, slug)
  }, [status.state, queryClient, slug])

  return (
    <PageCard
      title={project.name}
      hint={<span className="font-mono">{project.slug}</span>}
      badges={
        <div className="flex flex-wrap items-center gap-2">
          <IndexStatus status={project.index} />
          {project.singleRepository ? (
            <Badge variant="outline" className="text-muted-foreground">
              single repository
            </Badge>
          ) : null}
        </div>
      }
      actions={
        // "Refresh" as CONTEXT.md defines it, and for a project that has never been built too: the
        // first one clones rather than fetches, but it is the same action and a second name for it
        // would only suggest there are two. The label says what is happening rather than what was
        // asked for, because the work outlives the click and a page reopened mid-refresh must read
        // the same.
        <Button onClick={() => refresh.mutate()} disabled={running || refresh.isPending}>
          <RefreshCw />
          {running ? 'Refreshing…' : 'Refresh'}
        </Button>
      }
    >
      <div className="space-y-6">
        {/* A refusal — another refresh running, or too little disk — is the server's prose and
            belongs in the panel that shows it verbatim. What a refresh then does is the status's to
            report, on both pages: it is the one thing happening to this project either way. */}
        {refresh.error ? <ErrorPanel error={refresh.error} /> : null}
        <RefreshProgress status={status} />
        {children}
      </div>
    </PageCard>
  )
}
