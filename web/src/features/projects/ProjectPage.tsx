import { useParams } from '@tanstack/react-router'
import { ProjectCard } from '@/features/projects/ProjectCard'
import { ProjectOverview } from '@/features/projects/ProjectOverview'

/**
 * What a project's index holds, in one answer (CONTEXT.md, _Overview_). The page an operator lands
 * on, and the project's own path: how the project is configured is its own route beside this one
 * (`ProjectSettings.tsx`), because the sidebar lists them as two items and a reader following one
 * of those should arrive somewhere with its own address.
 */
export function ProjectPage() {
  const { project } = useParams({ from: '/projects/$project/' })

  return (
    <ProjectCard project={project}>
      <ProjectOverview project={project} />
    </ProjectCard>
  )
}
