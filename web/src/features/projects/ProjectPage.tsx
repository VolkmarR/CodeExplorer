import { useEffect } from 'react'
import { useMutation, useQueryClient, useSuspenseQuery } from '@tanstack/react-query'
import { useNavigate, useParams } from '@tanstack/react-router'
import { api } from '@/lib/api'
import { ConfirmDialog } from '@/components/ConfirmDialog'
import { ErrorPanel } from '@/components/ErrorPanel'
import { IndexStatus } from '@/features/projects/IndexStatus'
import { RefreshProgress } from '@/features/refresh/RefreshProgress'
import { RepositoryTable } from '@/features/projects/RepositoryTable'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { toast } from '@/components/ui/toast'
import { invalidateProject, projectQuery, projectsQuery } from '@/features/projects/queries'
import { refreshStatusQuery } from '@/features/refresh/queries'

/**
 * One project: what its index holds, the repositories it is built from, and the operator actions on
 * both. A refresh fetches every repository and rebuilds beside the live index, so the button hands
 * the work off and the page follows it by polling; searches keep answering the whole time.
 */
export function ProjectPage() {
  const { project: slug } = useParams({ from: '/projects/$project/' })
  const { data: project } = useSuspenseQuery(projectQuery(slug))
  const { data: status } = useSuspenseQuery(refreshStatusQuery(slug))
  const queryClient = useQueryClient()
  const navigate = useNavigate()

  const running = status.state === 'Queued' || status.state === 'Running'

  const refresh = useMutation({
    mutationFn: () => api.refresh(slug),
    // The status the POST answers with is the first poll, so the progress line appears without
    // waiting a second for the interval to come round.
    onSuccess: (started) => queryClient.setQueryData(refreshStatusQuery(slug).queryKey, started),
  })

  // The index only changes when a refresh finishes, and the status is the only thing that says so.
  // Keyed on the state alone: a second refresh passes through Queued and Running on its way back to
  // Succeeded, so this fires once per refresh rather than on every poll reporting the same one.
  useEffect(() => {
    if (status.state === 'Succeeded') void invalidateProject(queryClient, slug)
  }, [status.state, queryClient, slug])

  const remove = useMutation({
    mutationFn: () => api.removeProject(slug),
    onSuccess: async () => {
      await queryClient.invalidateQueries(projectsQuery())
      // The toast outlives the page, which is the point: the list this lands on shows the project
      // gone and nothing else, so the toast is what says it was this click that did it.
      toast.add({ title: `Deleted ${project.name}`, type: 'success' })
      await navigate({ to: '/' })
    },
  })

  return (
    <div className="space-y-8">
      <div className="flex flex-wrap items-start justify-between gap-4">
        <div>
          <h1 className="text-2xl font-semibold tracking-tight">{project.name}</h1>
          <p className="mt-1 font-mono text-xs text-muted-foreground">{project.slug}</p>
          <div className="mt-3 flex flex-wrap items-center gap-2">
            <IndexStatus status={project.index} />
            {project.singleRepository ? (
              <Badge variant="outline" className="text-muted-foreground">
                single repository
              </Badge>
            ) : null}
          </div>
        </div>
        {/* "Refresh" as CONTEXT.md defines it, and for a project that has never been built too: the
            first one clones rather than fetches, but it is the same action and a second name for it
            would only suggest there are two. The label says what is happening rather than what was
            asked for, because the work outlives the click and a page reopened mid-refresh must read
            the same. It stands alone up here: it is the one action on this page done more than once,
            and deleting the project sits at the bottom where a slip of the hand does not reach it. */}
        <Button onClick={() => refresh.mutate()} disabled={running || refresh.isPending}>
          {running ? 'Refreshing…' : 'Refresh'}
        </Button>
      </div>

      {/* A refusal — another refresh running, or too little disk — is the server's prose and belongs
          in the panel that shows it verbatim. What a refresh then does is the status's to report. */}
      {refresh.error ? <ErrorPanel error={refresh.error} /> : null}
      {remove.error ? <ErrorPanel error={remove.error} /> : null}
      <RefreshProgress status={status} />

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
