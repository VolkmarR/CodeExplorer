import { useSuspenseQuery } from '@tanstack/react-query'
import { Link } from '@tanstack/react-router'
import { Plus, Settings } from 'lucide-react'
import { treeSearch } from '@/features/files/browseParams'
import { IndexStatus } from '@/features/projects/IndexStatus'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'
import { projectsQuery } from '@/features/projects/queries'

/** Stretches the title link over its whole card, so the card is one target. */
const CARD_LINK = 'after:absolute after:inset-0 hover:underline'

/**
 * Every project, with what its index holds. MCP has no discovery — an agent connects to a URL it was
 * given — so this list is the only place a project becomes visible at all.
 *
 * A project card leads to its files rather than to its settings: browsing code is the daily use and
 * editing a project is rare, so creation hides behind a button and editing behind a per-card one.
 */
export function ProjectList() {
  const { data: projects } = useSuspenseQuery(projectsQuery())

  return (
    <div className="space-y-6">
      <div className="flex flex-wrap items-start justify-between gap-4">
        <div>
          <h1 className="text-2xl font-semibold tracking-tight">Projects</h1>
          <p className="mt-1 text-sm text-muted-foreground">
            A project is what an agent connects to, and what a search spans.
          </p>
        </div>
        {/* The one thing to do on this page that is not opening a project, so it gets the filled
            button; the per-card Settings stay quiet beside it. */}
        <Button render={<Link to="/projects/new" />}>
          <Plus />
          New project
        </Button>
      </div>

      {projects.length === 0 ? (
        <p className="text-sm text-muted-foreground">
          No projects yet. Create one with New project.
        </p>
      ) : (
        <ul className="space-y-3">
          {projects.map((project) => (
            <li key={project.slug}>
              {/* The whole card is the browse target, so the title link is stretched over it with a
                  pseudo-element. The settings button needs its own stacking context to stay
                  clickable; nesting it inside the link would be invalid instead. */}
              <Card size="sm" className="relative transition-colors hover:bg-muted/40">
                <CardHeader className="flex flex-row items-baseline justify-between gap-4">
                  <CardTitle>
                    {/* Nothing to browse before the first build, so an unbuilt project leads to its
                        settings — where the repositories and the build button are — instead. */}
                    {project.index.builtAt ? (
                      <Link
                        to="/projects/$project/files"
                        params={{ project: project.slug }}
                        search={treeSearch()}
                        className={CARD_LINK}
                      >
                        {project.name}
                      </Link>
                    ) : (
                      <Link
                        to="/projects/$project/settings"
                        params={{ project: project.slug }}
                        className={CARD_LINK}
                      >
                        {project.name}
                      </Link>
                    )}
                    <span className="ml-2 font-mono text-xs font-normal text-muted-foreground">
                      {project.slug}
                    </span>
                  </CardTitle>
                  <div className="flex items-center gap-3">
                    <span className="text-sm text-muted-foreground">
                      {project.repositories}{' '}
                      {project.repositories === 1 ? 'repository' : 'repositories'}
                    </span>
                    <Button
                      render={
                        <Link to="/projects/$project/settings" params={{ project: project.slug }} />
                      }
                      variant="ghost"
                      size="sm"
                      className="relative"
                    >
                      <Settings />
                      Settings
                    </Button>
                  </div>
                </CardHeader>
                <CardContent>
                  <IndexStatus status={project.index} />
                </CardContent>
              </Card>
            </li>
          ))}
        </ul>
      )}
    </div>
  )
}
