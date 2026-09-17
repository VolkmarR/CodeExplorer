import { useSuspenseQuery } from '@tanstack/react-query'
import { AuthorsCard } from '@/features/projects/AuthorsCard'
import { LanguageShares } from '@/features/projects/LanguageShares'
import { MostChangedCard } from '@/features/projects/MostChangedCard'
import { TopLevelCard } from '@/features/projects/TopLevelCard'
import { projectOverviewQuery } from '@/features/projects/queries'

/**
 * What the build computed about the project as a whole: what it is written in, how it is laid out,
 * what is biggest in it, where work has been happening and who has been doing it.
 *
 * It is the same row the `project_overview` tool answers from, so an operator opening a project sees
 * what an agent connecting to it sees — including when there is nothing to see: a project with no
 * index says why, in the server's own words, rather than leaving a gap the reader has to interpret.
 */
export function ProjectOverview({ project }: { project: string }) {
  const { data } = useSuspenseQuery(projectOverviewQuery(project))
  if (!data.overview) {
    return (
      <section aria-label="Overview" className="rounded-lg border px-5 py-4">
        <p className="text-sm text-muted-foreground">{data.unavailable}</p>
      </section>
    )
  }

  return (
    <div className="grid gap-4 lg:grid-cols-2">
      <LanguageShares overview={data.overview} />
      <TopLevelCard project={project} overview={data.overview} />
      <MostChangedCard project={project} overview={data.overview} />
      <AuthorsCard overview={data.overview} />
    </div>
  )
}
