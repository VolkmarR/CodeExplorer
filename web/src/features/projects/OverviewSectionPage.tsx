import { useParams, useSearch } from '@tanstack/react-router'
import { PageCard } from '@/components/PageCard'
import { ProjectOverview } from '@/features/projects/ProjectOverview'

/**
 * The two overview pages after the first, which differ in their title and in which cards
 * `ProjectOverview` draws and in nothing else. The first is `ProjectPage`, which carries the
 * project's card and its refresh as well.
 */
const SECTIONS = {
  activity: { hint: 'What changed in the window, and who changed it', title: 'Activity' },
  risk: { hint: 'Files and folders that are expensive to change', title: 'Risk' },
} as const

export function OverviewSectionPage({ page }: { page: keyof typeof SECTIONS }) {
  const from = page === 'activity' ? '/projects/$project/activity' : '/projects/$project/risk'
  const { project } = useParams({ from })
  const search = useSearch({ from })

  return (
    <PageCard title={SECTIONS[page].title} hint={SECTIONS[page].hint}>
      <ProjectOverview project={project} page={page} search={search} />
    </PageCard>
  )
}
