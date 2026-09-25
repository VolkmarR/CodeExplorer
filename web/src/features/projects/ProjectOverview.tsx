import { useSuspenseQuery } from '@tanstack/react-query'
import { useNavigate } from '@tanstack/react-router'
import { AuthorsCard } from '@/features/projects/AuthorsCard'
import { AuthorsPerFileCard } from '@/features/projects/AuthorsPerFileCard'
import { FileChangesCard } from '@/features/projects/FileChangesCard'
import { FolderCouplingCard } from '@/features/projects/FolderCouplingCard'
import { HotspotsCard } from '@/features/projects/HotspotsCard'
import { LanguageShares } from '@/features/projects/LanguageShares'
import { LargestFilesCard } from '@/features/projects/LargestFilesCard'
import { MostChangedCard } from '@/features/projects/MostChangedCard'
import { OverviewControls } from '@/features/projects/OverviewControls'
import { TopLevelCard } from '@/features/projects/TopLevelCard'
import { projectOverviewQuery, projectQuery } from '@/features/projects/queries'
import { overviewSearch, type OverviewParameters } from '@/lib/urls/overviewParams'

/** The three pages the overview is split over, each a route of its own. */
export type OverviewPage = 'code' | 'activity' | 'risk'

const PAGE_ROUTES = {
  activity: '/projects/$project/activity',
  code: '/projects/$project',
  risk: '/projects/$project/risk',
} as const satisfies Record<OverviewPage, string>

/**
 * One page of the project as a whole. Code is what it is written in and how it is laid out; Activity
 * is where work has been happening, who has been doing it, and whether the codebase is growing; Risk
 * is which large files keep changing, which files nobody owns, which folders change together, and\n * which files are the largest.
 *
 * All three read the one overview answer, so moving between them with the same filters is a cache
 * hit rather than a second computation. It is computed live from the index (#216), over the filter
 * bar's window and repository and without the paths the project's settings exclude — so it is not
 * the stored row `project_overview` answers an agent from, and once a filter or an exclusion applies
 * it may show different numbers. What it does share with the tool is the explanation when there is
 * nothing to see: a project with no index says why, in the server's own words.
 */
export function ProjectOverview({
  project,
  page,
  search,
}: {
  project: string
  page: OverviewPage
  search: OverviewParameters
}) {
  const navigate = useNavigate()
  const { data: detail } = useSuspenseQuery(projectQuery(project))
  const { data } = useSuspenseQuery(projectOverviewQuery(project, search))

  function show(change: Partial<OverviewParameters>) {
    void navigate({
      params: { project },
      search: overviewSearch(search, change),
      to: PAGE_ROUTES[page],
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
        // Languages and the layout are counted over the whole index, so a window would change nothing.
        showWindow={page !== 'code'}
        onChange={show}
      />
    ) : null

  if (!data.overview) {
    return (
      <div className="space-y-6">
        {controls}
        <section aria-label="Overview" className="rounded-lg border px-5 py-4">
          <p className="text-sm text-muted-foreground">{data.unavailable}</p>
        </section>
      </div>
    )
  }

  const { excluded, overview, cards } = data
  return (
    <div className="space-y-6">
      {controls}
      {/* One column on Code: the language bar reads best at full width, and the layout is a list. */}
      <div className={page === 'code' ? 'grid gap-4' : 'grid gap-4 lg:grid-cols-2'}>
        {page === 'code' ? (
          <>
            <LanguageShares project={project} overview={overview} excluded={excluded?.files} />
            <TopLevelCard project={project} overview={overview} excluded={excluded?.files} />
          </>
        ) : null}
        {page === 'activity' ? (
          <>
            {/* Across the whole row: two years of months need the width to read as a trend. */}
            {cards ? (
              <div className="lg:col-span-2">
                <FileChangesCard changes={cards.fileChanges} />
              </div>
            ) : null}
            <MostChangedCard
              project={project}
              repository={search.repository}
              overview={overview}
              excluded={excluded?.changedFiles}
            />
            <AuthorsCard
              project={project}
              overview={overview}
              excluded={excluded?.committedFiles}
            />
          </>
        ) : null}
        {page === 'risk' && cards ? (
          <>
            <HotspotsCard project={project} churn={overview.churn} hotspots={cards.hotspots} />
            <AuthorsPerFileCard
              project={project}
              churn={overview.churn}
              authors={cards.authorsPerFile}
            />
            <FolderCouplingCard
              repository={search.repository}
              churn={overview.churn}
              coupling={cards.folderCoupling}
            />
          </>
        ) : null}
        {page === 'risk' ? (
          <LargestFilesCard project={project} overview={overview} excluded={excluded?.files} />
        ) : null}
      </div>
    </div>
  )
}
