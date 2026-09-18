import { Activity, BarChart3, Clock, FolderTree, Search, Settings } from 'lucide-react'
import { churnSearch } from '@/features/churn/churnParams'
import { treeSearch } from '@/features/files/browseParams'
import { historySearch } from '@/features/history/historyParams'
import { searchSearch } from '@/features/search/searchParams'

/** The sidebar's items, in the order it lists them. */
export type View = 'overview' | 'files' | 'search' | 'history' | 'churn' | 'settings'

/**
 * Every view of a project: what it is called, what it is drawn as, and where it goes. One table
 * rather than six near-identical blocks in the sidebar, so that adding a view is an entry here
 * beside its matcher below, and not a block to copy in the sidebar plus a name to remember to add
 * for the breadcrumb.
 *
 * `link` is spread onto a `Link`, which supplies the `params`; the search for each view comes from
 * that view's own params module, so no default is spelled out here either. The two views that read
 * nothing from the URL beyond the project carry no search at all.
 */
export const PROJECT_VIEWS = [
  {
    Icon: Activity,
    label: 'Overview',
    link: { to: '/projects/$project' },
    view: 'overview',
  },
  {
    Icon: FolderTree,
    label: 'Files',
    link: { search: treeSearch(), to: '/projects/$project/files' },
    view: 'files',
  },
  {
    Icon: Search,
    label: 'Search',
    link: { search: searchSearch(), to: '/projects/$project/search' },
    view: 'search',
  },
  {
    Icon: Clock,
    label: 'History',
    link: { search: historySearch(), to: '/projects/$project/history' },
    view: 'history',
  },
  {
    Icon: BarChart3,
    label: 'Churn',
    link: { search: churnSearch(), to: '/projects/$project/churn' },
    view: 'churn',
  },
  {
    Icon: Settings,
    label: 'Settings',
    link: { to: '/projects/$project/settings' },
    view: 'settings',
  },
] as const satisfies readonly { Icon: typeof Activity; label: string; link: object; view: View }[]

/** What each view is called, for whatever has to name one without listing them all. */
export const VIEW_NAMES: Record<View, string> = Object.fromEntries(
  PROJECT_VIEWS.map((item) => [item.view, item.label]),
) as Record<View, string>

/** The project-relative segment each view sits at, from the table above. Empty for the overview. */
const VIEW_SEGMENTS = new Map<string, View>(
  PROJECT_VIEWS.map((item) => [item.link.to.replace(/^\/projects\/\$project\/?/, ''), item.view]),
)

/**
 * Which view a project-relative segment belongs to where it is not the view's own name. Reading a
 * file is part of Files and lives at its own route — `/file`, not under `/files` — so the reader of
 * a file would otherwise stand on no item at all.
 */
const VIEW_ALIASES = new Map<string, View>([['file', 'files']])

/**
 * Which sidebar item is lit, decided from the path rather than by each link's own active state,
 * because of the one exception above: a link cannot know that `/file` belongs to Files.
 *
 * Derived from `PROJECT_VIEWS` rather than a chain of comparisons, so adding a view is the one entry
 * in that table and not an entry plus a branch here — which is what the second edit being caught
 * only by a test used to mean.
 *
 * Every view is decided from the path alone, which is why this takes nothing else. It read the
 * project page's tab as a second argument while Overview and Settings shared one route, and the
 * cost was that the frame re-read a search param on all six pages to light one item.
 *
 * A module of its own rather than a function inside the sidebar: it needs none of the component's
 * state to decide, and every case it gets wrong is a case worth a test.
 */
export function activeView(pathname: string): View | null {
  // A trailing slash is the same page, and the router hands one out for an index route.
  const path = pathname.replace(/\/$/, '')

  // `/projects/new` is slug-shaped and is not a project: it is the form that makes one, and a static
  // segment beats `$project` in the router for the same reason.
  if (path === '/projects/new') return null

  const inProject = /^\/projects\/[^/]+(?:\/(.*))?$/.exec(path)
  if (inProject === null) return null

  const segment = inProject[1] ?? ''
  return VIEW_SEGMENTS.get(segment) ?? VIEW_ALIASES.get(segment) ?? null
}
