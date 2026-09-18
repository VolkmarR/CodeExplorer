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

/**
 * Which sidebar item is lit, decided from the path rather than by each link's `activeProps`,
 * because one of the six does not sit where its name suggests: reading a file is part of Files and
 * lives at its own route (`/file`, not under `/files`). A link's own active state cannot know that,
 * and would leave the reader of a file standing on no item at all.
 *
 * Every view is decided from the path alone, which is why this takes nothing else. It read the
 * project page's tab as a second argument while Overview and Settings shared one route, and the
 * cost was that the frame re-read a search param on all six pages to light one item.
 *
 * A module of its own rather than a function inside the sidebar: it is the one part of the frame
 * that grows with every view added, it needs none of the component's state to decide, and every
 * case it gets wrong is a case worth a test.
 */
export function activeView(pathname: string): View | null {
  // A trailing slash is the same page, and the router hands one out for an index route.
  const path = pathname.replace(/\/$/, '')

  if (path.endsWith('/search')) return 'search'
  if (path.endsWith('/history')) return 'history'
  if (path.endsWith('/churn')) return 'churn'
  if (path.endsWith('/settings')) return 'settings'
  if (path.endsWith('/files') || path.endsWith('/file')) return 'files'

  // `/projects/new` reaches here as a slug-shaped path and is not a project: it is the form that
  // makes one, and a static segment beats `$project` in the router for the same reason.
  if (path === '/projects/new') return null

  return /^\/projects\/[^/]+$/.test(path) ? 'overview' : null
}
