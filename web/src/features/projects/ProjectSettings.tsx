import { useMutation, useQueryClient, useSuspenseQuery } from '@tanstack/react-query'
import { useNavigate, useParams } from '@tanstack/react-router'
import { api } from '@/lib/api'
import { ConfirmDialog } from '@/components/ConfirmDialog'
import { ErrorPanel } from '@/components/ErrorPanel'
import { ProjectCard } from '@/features/projects/ProjectCard'
import { RepositoryTable } from '@/features/projects/RepositoryTable'
import { projectQuery, projectsQuery } from '@/features/projects/queries'
import { toast } from '@/components/ui/toast'

/**
 * How the project is configured: which repositories it is built from, and how to unmake it.
 *
 * Its own route rather than a tab on the overview. The two were one page told apart by a search
 * param, and the cost was spread over the whole frame: the sidebar could not light an item from the
 * path, and the overview — the page's heaviest read — was fetched or not by a branch in one loader.
 *
 * It owns the deletion rather than taking it apart into four props: nothing on the overview uses it,
 * and passing `isPending`, `error` and a callback separately spreads one mutation across two
 * components.
 */
export function ProjectSettings() {
  const { project: slug } = useParams({ from: '/projects/$project/settings' })
  const { data: project } = useSuspenseQuery(projectQuery(slug))
  const queryClient = useQueryClient()
  const navigate = useNavigate()

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
    <ProjectCard project={slug}>
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
    </ProjectCard>
  )
}
