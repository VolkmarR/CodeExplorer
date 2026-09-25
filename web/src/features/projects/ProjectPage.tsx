import { useParams, useSearch } from '@tanstack/react-router'
import { PageCard } from '@/components/PageCard'
import { ProjectCard } from '@/features/projects/ProjectCard'
import { ProjectOverview } from '@/features/projects/ProjectOverview'

/**
 * What a project's index holds (CONTEXT.md, _Overview_), split over three pages because one held
 * eight cards and scrolled past most of them: what the code is, what is changing in it, and where it
 * is expensive to change. This is the first, the page an operator lands on and the project's own
 * path, so it is also the one that carries the project's card and its refresh.
 *
 * How the project is configured is its own route beside these (`ProjectSettings.tsx`).
 */
export function ProjectPage() {
  const { project } = useParams({ from: '/projects/$project/' })
  const search = useSearch({ from: '/projects/$project/' })

  return (
    <ProjectCard project={project}>
      <ProjectOverview project={project} page="code" search={search} />
    </ProjectCard>
  )
}

/** What changed over the filter bar's window, and who changed it. */
export function ActivityPage() {
  const { project } = useParams({ from: '/projects/$project/activity' })
  const search = useSearch({ from: '/projects/$project/activity' })

  return (
    <PageCard title="Activity" hint="What changed in the window, and who changed it">
      <ProjectOverview project={project} page="activity" search={search} />
    </PageCard>
  )
}

/** The files and folders that are expensive to change. */
export function RiskPage() {
  const { project } = useParams({ from: '/projects/$project/risk' })
  const search = useSearch({ from: '/projects/$project/risk' })

  return (
    <PageCard title="Risk" hint="Files and folders that are expensive to change">
      <ProjectOverview project={project} page="risk" search={search} />
    </PageCard>
  )
}
