import { useSuspenseQuery } from '@tanstack/react-query'
import { Link } from '@tanstack/react-router'
import { IndexStatus } from '@/features/projects/IndexStatus'
import { NewProjectForm } from '@/features/projects/NewProjectForm'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'
import { projectsQuery } from '@/features/projects/queries'

/**
 * Every project, with what its index holds. MCP has no discovery — an agent connects to a URL it was
 * given — so this list is the only place a project becomes visible at all.
 */
export function ProjectList() {
  const { data: projects } = useSuspenseQuery(projectsQuery())
  return (
    <div className="space-y-8">
      <div>
        <h1 className="text-2xl font-semibold tracking-tight">Projects</h1>
        <p className="mt-1 text-sm text-muted-foreground">
          A project is what an agent connects to, and what a search spans.
        </p>
      </div>

      {projects.length === 0 ? (
        <p className="text-sm text-muted-foreground">No projects yet. Create one below.</p>
      ) : (
        <ul className="space-y-3">
          {projects.map((project) => (
            <li key={project.slug}>
              <Card>
                <CardHeader className="flex flex-row items-baseline justify-between gap-4">
                  <CardTitle>
                    <Link
                      to="/projects/$project"
                      params={{ project: project.slug }}
                      className="hover:underline"
                    >
                      {project.name}
                    </Link>
                    <span className="ml-2 font-mono text-sm font-normal text-muted-foreground">
                      {project.slug}
                    </span>
                  </CardTitle>
                  <span className="text-sm text-muted-foreground">
                    {project.repositories}{' '}
                    {project.repositories === 1 ? 'repository' : 'repositories'}
                  </span>
                </CardHeader>
                <CardContent>
                  <IndexStatus status={project.index} />
                </CardContent>
              </Card>
            </li>
          ))}
        </ul>
      )}

      <NewProjectForm />
    </div>
  )
}
