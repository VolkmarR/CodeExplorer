import { useParams, useSearch } from '@tanstack/react-router'
import { ProjectCard } from '@/features/projects/ProjectCard'
import { ProjectOverview } from '@/features/projects/ProjectOverview'

/**
 * What a project's index holds (CONTEXT.md, _Overview_), split over three pages because one held
 * eight cards and scrolled past most of them: what the code is, what is changing in it, and where it
 * is expensive to change. This is the first, the page an operator lands on and the project's own
 * path, so it is also the one that carries the project's card and its refresh.
 *
 * The other two are `OverviewSectionPage`. How the project is configured is its own route beside
 * these (`ProjectSettings.tsx`).
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
