import { useSuspenseQuery } from '@tanstack/react-query'
import { useNavigate, useSearch } from '@tanstack/react-router'
import { AuthorsCard } from '@/features/projects/AuthorsCard'
import { AuthorsPerFileCard } from '@/features/projects/AuthorsPerFileCard'
import { HotspotsCard } from '@/features/projects/HotspotsCard'
import { LanguageShares } from '@/features/projects/LanguageShares'
import { MostChangedCard } from '@/features/projects/MostChangedCard'
import { OverviewControls } from '@/features/projects/OverviewControls'
import { TopLevelCard } from '@/features/projects/TopLevelCard'
import { projectOverviewQuery, projectQuery } from '@/features/projects/queries'
import { overviewSearch, type OverviewParameters } from '@/lib/urls/overviewParams'

/**
 * The project as a whole: what it is written in, how it is laid out, what is biggest in it, where
 * work has been happening, who has been doing it, which large files keep changing, and which files
 * nobody owns.
 *
 * Computed live from the index for this page (#216), over the filter bar's window and repository and
 * without the paths the project's settings exclude — so it is not the stored row `project_overview`
 * answers an agent from, and once a filter or an exclusion applies it may show different numbers.
 * What it does share with the tool is the explanation when there is nothing to see: a project with no
 * index says why, in the server's own words, rather than leaving a gap the reader has to interpret.
 */
export function ProjectOverview({ project }: { project: string }) {
  const search = useSearch({ from: '/projects/$project/' })
  const navigate = useNavigate()
  const { data: detail } = useSuspenseQuery(projectQuery(project))
  const { data } = useSuspenseQuery(projectOverviewQuery(project, search))

  function show(change: Partial<OverviewParameters>) {
    void navigate({
      params: { project },
      search: overviewSearch(search, change),
      to: '/projects/$project',
    })
  }

  // Offered whenever there is an overview to filter, and also where a filter is why there is none:
  // a repository the project does not have is answered in prose, and the bar is the way back out.
  const controls =
    data.overview || search.repository ? (
      <OverviewControls
        search={search}
        repositories={detail.repositories}
        excludedPatterns={data.excludedPatterns}
        onChange={show}
      />
    ) : null

  if (!data.overview) {
    return (
      <>
        {controls}
        <section aria-label="Overview" className="rounded-lg border px-5 py-4">
          <p className="text-sm text-muted-foreground">{data.unavailable}</p>
        </section>
      </>
    )
  }

  const { excluded } = data
  return (
    <>
      {controls}
      <div className="grid gap-4 lg:grid-cols-2">
        <LanguageShares project={project} overview={data.overview} excluded={excluded?.files} />
        <TopLevelCard project={project} overview={data.overview} excluded={excluded?.files} />
        <MostChangedCard
          project={project}
          repository={search.repository}
          overview={data.overview}
          excluded={excluded?.changedFiles}
        />
        <AuthorsCard
          project={project}
          overview={data.overview}
          excluded={excluded?.committedFiles}
        />
        {data.cards && (
          <>
            <HotspotsCard
              project={project}
              churn={data.overview.churn}
              hotspots={data.cards.hotspots}
            />
            <AuthorsPerFileCard
              project={project}
              churn={data.overview.churn}
              authors={data.cards.authorsPerFile}
            />
          </>
        )}
      </div>
    </>
  )
}
