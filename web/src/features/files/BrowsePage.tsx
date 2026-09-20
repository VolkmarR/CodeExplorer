import { Link, useParams, useSearch } from '@tanstack/react-router'
import { PageCard } from '@/components/PageCard'
import { BrowseFilter } from '@/features/files/BrowseFilter'
import { FileList } from '@/features/files/FileList'
import { FileTree } from '@/features/files/FileTree'
import { globSearch, treeSearch } from '@/lib/urls/browseParams'
import { Tabs, TabsList, TabsTrigger } from '@/components/ui/tabs'

/**
 * Browsing a project's index: what is in there, rather than what matches a query. The pair with
 * search, and the answer to "I don't know what to search for yet".
 *
 * The two modes are tabs because that is what they are — one question asked two ways — and each is
 * a link, because which was asked is in the URL and has to stay shareable. An empty glob is the
 * tree; any glob is the flat list.
 */
export function BrowsePage() {
  const { project } = useParams({ from: '/projects/$project/files' })
  const search = useSearch({ from: '/projects/$project/files' })
  const mode = search.glob === '' ? 'tree' : 'glob'

  return (
    <PageCard
      title="Files"
      hint="what the last build put in the index"
      tabs={
        <Tabs value={mode}>
          <TabsList>
            <TabsTrigger
              value="tree"
              render={
                <Link
                  to="/projects/$project/files"
                  params={{ project }}
                  search={treeSearch(search.path)}
                >
                  Tree
                </Link>
              }
            />
            <TabsTrigger
              value="glob"
              render={
                <Link
                  to="/projects/$project/files"
                  params={{ project }}
                  // A glob that finds everything where there is none yet, so the tab lands on a
                  // listing rather than on the tree it is supposed to be the alternative to. The
                  // builder starts it at page 1, because arriving from the tree is arriving at a
                  // listing not yet walked.
                  search={globSearch(
                    search.glob === '' ? '*' : search.glob,
                    search.repository,
                    search.path,
                  )}
                >
                  By glob
                </Link>
              }
            />
          </TabsList>
        </Tabs>
      }
    >
      <div className="space-y-4">
        {mode === 'glob' ? <BrowseFilter project={project} search={search} /> : null}
        {mode === 'tree' ? (
          <FileTree project={project} path={search.path} />
        ) : (
          <FileList project={project} search={search} />
        )}
      </div>
    </PageCard>
  )
}
