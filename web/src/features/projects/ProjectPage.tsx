import { useEffect } from 'react'
import { useMutation, useQueryClient, useSuspenseQuery } from '@tanstack/react-query'
import { Link, useNavigate, useParams, useSearch } from '@tanstack/react-router'
import { RefreshCw } from 'lucide-react'
import type { ProjectDetail } from '@/lib/api'
import { api } from '@/lib/api'
import { ConfirmDialog } from '@/components/ConfirmDialog'
import { ErrorPanel } from '@/components/ErrorPanel'
import { PageCard } from '@/components/PageCard'
import { IndexStatus } from '@/features/projects/IndexStatus'
import { ProjectOverview } from '@/features/projects/ProjectOverview'
import { projectSearch } from '@/features/projects/projectParams'
import { RefreshProgress } from '@/features/refresh/RefreshProgress'
import { RepositoryTable } from '@/features/projects/RepositoryTable'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Tabs, TabsList, TabsTrigger } from '@/components/ui/tabs'
import { toast } from '@/components/ui/toast'
import { invalidateProject, projectQuery, projectsQuery } from '@/features/projects/queries'
import { isRefreshRunning, refreshStatusQuery } from '@/features/refresh/queries'
import { useRefreshProject } from '@/features/refresh/useRefreshProject'

/**
 * One project, in two halves the sidebar lists as two items: what its index holds, and how the
 * project is configured. They are one route because the server answers both from one row, and the
 * tab is a search param so each half is a link of its own (`projectParams.ts`).
 *
 * A refresh fetches every repository and rebuilds beside the live index, so the button hands the
 * work off and the page follows it by polling; searches keep answering the whole time.
 */
export function ProjectPage() {
  const { project: slug } = useParams({ from: '/projects/$project/' })
  const { tab } = useSearch({ from: '/projects/$project/' })
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
      tabs={
        <Tabs value={tab}>
          <TabsList>
            <TabsTrigger
              value="overview"
              render={
                <Link
                  to="/projects/$project"
                  params={{ project: slug }}
                  search={projectSearch('overview')}
                >
                  Overview
                </Link>
              }
            />
            <TabsTrigger
              value="settings"
              render={
                <Link
                  to="/projects/$project"
                  params={{ project: slug }}
                  search={projectSearch('settings')}
                >
                  Settings
                </Link>
              }
            />
          </TabsList>
        </Tabs>
      }
    >
      <div className="space-y-6">
        {/* A refusal — another refresh running, or too little disk — is the server's prose and
            belongs in the panel that shows it verbatim. What a refresh then does is the status's to
            report, on both tabs: it is the one thing happening to this project either way. */}
        {refresh.error ? <ErrorPanel error={refresh.error} /> : null}
        <RefreshProgress status={status} />

        {tab === 'overview' ? (
          <ProjectOverview project={slug} />
        ) : (
          <SettingsTab project={project} />
        )}
      </div>
    </PageCard>
  )
}

/**
 * How the project is configured: which repositories it is built from, and how to unmake it.
 *
 * It owns the deletion rather than taking it apart into four props: nothing on the overview half
 * uses it, and passing `isPending`, `error` and a callback separately spread one mutation across
 * two components.
 */
function SettingsTab({ project }: { project: ProjectDetail }) {
  const queryClient = useQueryClient()
  const navigate = useNavigate()

  const remove = useMutation({
    mutationFn: () => api.removeProject(project.slug),
    onSuccess: async () => {
      await queryClient.invalidateQueries(projectsQuery())
      // The toast outlives the page, which is the point: the list this lands on shows the project
      // gone and nothing else, so the toast is what says it was this click that did it.
      toast.add({ title: `Deleted ${project.name}`, type: 'success' })
      await navigate({ to: '/' })
    },
  })

  return (
    <div className="space-y-6">
      {remove.error ? <ErrorPanel error={remove.error} /> : null}
      <RepositoryTable project={project} />

      <section
        aria-labelledby="danger-title"
        className="flex flex-wrap items-center justify-between gap-4 rounded-lg border border-destructive/40 px-5 py-4"
      >
        <div>
          <h2 id="danger-title" className="text-sm font-medium text-destructive">
            Delete this project
          </h2>
          <p className="mt-1 text-sm text-muted-foreground">
            Removes the index and the local copies of its repositories. An agent connected to this
            project stops getting answers.
          </p>
        </div>
        <ConfirmDialog
          trigger="Delete project"
          title={`Delete ${project.name}?`}
          description={
            <>
              Its index and the local copies of{' '}
              {project.repositories.length === 0
                ? 'its repositories'
                : project.repositories.map((r) => r.slug).join(', ')}{' '}
              are removed. This cannot be undone.
            </>
          }
          action="Delete project"
          disabled={remove.isPending}
          onConfirm={() => remove.mutate()}
        />
      </section>
    </div>
  )
}
