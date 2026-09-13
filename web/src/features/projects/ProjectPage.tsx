import { useMutation, useQueryClient, useSuspenseQuery } from '@tanstack/react-query'
import { useNavigate, useParams } from '@tanstack/react-router'
import { api } from '@/lib/api'
import { BuildSummary } from '@/features/projects/BuildSummary'
import { ErrorPanel } from '@/components/ErrorPanel'
import { IndexStatus } from '@/features/projects/IndexStatus'
import { NewRepositoryForm } from '@/features/projects/NewRepositoryForm'
import { RepositoryTable } from '@/features/projects/RepositoryTable'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'
import { invalidateProject, projectQuery, projectsQuery } from '@/features/projects/queries'

/**
 * One project: what its index holds, the repositories it is built from, and the operator actions on
 * both. A build runs inside the request until #8 makes it a shadow build and a swap, so the button
 * blocks and says so rather than pretending to be a background job.
 */
export function ProjectPage() {
  const { project: slug } = useParams({ from: '/projects/$project/' })
  const { data: project } = useSuspenseQuery(projectQuery(slug))
  const queryClient = useQueryClient()
  const navigate = useNavigate()

  const build = useMutation({
    mutationFn: () => api.index(slug),
    onSuccess: () => invalidateProject(queryClient, slug),
  })

  const remove = useMutation({
    mutationFn: () => api.removeProject(slug),
    onSuccess: async () => {
      await queryClient.invalidateQueries(projectsQuery())
      await navigate({ to: '/' })
    },
  })

  return (
    <div className="space-y-8">
      <div className="flex flex-wrap items-start justify-between gap-4">
        <div>
          <h1 className="text-2xl font-semibold tracking-tight">{project.name}</h1>
          <p className="mt-1 font-mono text-sm text-muted-foreground">{project.slug}</p>
          <div className="mt-3 flex flex-wrap items-center gap-2">
            <IndexStatus status={project.index} />
            {project.singleRepository ? (
              <Badge variant="outline" className="text-muted-foreground">
                single repository
              </Badge>
            ) : null}
          </div>
        </div>
        <div className="flex gap-2">
          {/* "Build" or "Rebuild", never "Refresh": this reads the local copies as they already are
              and never fetches, so the word CONTEXT.md reserves for fetch-and-rebuild would promise
              commits the operator will not get. #8 makes it fetch, and takes the word with it. */}
          <Button onClick={() => build.mutate()} disabled={build.isPending}>
            {build.isPending ? 'Building…' : project.index.builtAt ? 'Rebuild' : 'Build'}
          </Button>
          <Button
            variant="destructive"
            disabled={remove.isPending}
            onClick={() => {
              if (globalThis.confirm(`Delete project '${slug}', its index and its local copies?`))
                remove.mutate()
            }}
          >
            Delete
          </Button>
        </div>
      </div>

      {build.error ? <ErrorPanel error={build.error} /> : null}
      {remove.error ? <ErrorPanel error={remove.error} /> : null}
      {build.data ? <BuildSummary summary={build.data} /> : null}

      <Card>
        <CardHeader>
          <CardTitle className="text-base">Repositories</CardTitle>
        </CardHeader>
        <CardContent>
          <RepositoryTable
            project={slug}
            repositories={project.repositories}
            builtAt={project.index.builtAt}
          />
        </CardContent>
      </Card>

      {/* A single-repository project takes its one and no more, and the declaration cannot be undone,
          so once it is full there is no form to show — only the reason there is none (ADR-0006). */}
      {project.singleRepository && project.repositories.length > 0 ? (
        <p className="text-sm text-muted-foreground">
          This is a single-repository project, so it holds the one repository above and cannot take
          another. Create a separate project for a second repository.
        </p>
      ) : (
        <NewRepositoryForm project={slug} singleRepository={project.singleRepository} />
      )}
    </div>
  )
}
