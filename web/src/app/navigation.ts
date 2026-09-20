import { Activity, BarChart3, Clock, FolderTree, Search, Settings } from 'lucide-react'
import { treeSearch } from '@/lib/urls/browseParams'
import { churnSearch } from '@/lib/urls/churnParams'
import { historySearch } from '@/lib/urls/historyParams'
import { searchSearch } from '@/lib/urls/searchParams'
import { asView, type View } from '@/lib/urls/views'

/**
 * Every view of a project: what it is called, what it is drawn as, and where it goes. One table
 * rather than six near-identical blocks in the sidebar, so that adding a view is an entry here
 * beside its matcher below, and not a block to copy in the sidebar plus a name to remember to add
 * for the breadcrumb.
 *
 * `link` is spread onto a `Link`, which supplies the `params`; the search for each view comes from
 * that view's own params module in `lib/urls`, so no default is spelled out here either. The two
 * views that read nothing from the URL beyond the project carry no search at all.
 *
 * The names themselves are `VIEWS` in `lib/urls/views.ts`, because a view's name travels in the URL
 * and the params modules validate it. `satisfies` below pins every row here to one of those names;
 * a name added there still needs a row here, since a view with no row is one the frame never draws.
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
 * The two pages that are not a view of their own: a file and a commit. Each lives at its own route
 * rather than under the view it belongs to — `/file`, not under `/files` — and each can be reached
 * from more than one of them, which is what makes them the only pages where "which item is lit" and
 * "how did the reader get here" can differ.
 *
 * `under` answers both questions' default in one place: it is the sidebar item the route belongs to,
 * and the view the trail falls back to when the link carried no origin. Said once, because a third
 * such page said twice would be two edits and the second is the one that gets forgotten.
 */
const LINKED_PAGES = [
  { segment: 'file', under: 'files' },
  { segment: 'commit', under: 'history' },
] as const satisfies readonly { segment: string; under: View }[]

/**
 * Which view a project-relative segment belongs to where it is not the view's own name, so the
 * reader of a file or a commit does not stand on no item at all.
 *
 * This is about the sidebar and not about the trail: which item is lit is a fact about the route,
 * and where the reader came from is a fact about the link they followed. `pageTrail` reads that.
 */
const VIEW_ALIASES = new Map<string, View>(LINKED_PAGES.map((page) => [page.segment, page.under]))

/**
 * One step of the breadcrumb below the project. A view is named from `VIEW_NAMES` and links to
 * itself; a commit and a file are named by what they are and are the page they sit on, so the
 * renderer decides which of them is a link from its position in the trail and not from its kind.
 */
export type Step =
  | { kind: 'view'; view: View }
  | { kind: 'commit'; sha: string }
  | { kind: 'file'; path: string }

/**
 * The trail below the project: which view, and what was opened under it. It reads the origin the
 * link carried rather than deriving everything from the path, which is what made a file opened from
 * a commit read `Projects › radix › Files`.
 *
 * `activeView` still decides which sidebar item is lit, and deliberately still from the path alone:
 * the item says which part of the app this route belongs to, and the trail says how the reader got
 * here. They agree on every page but the two that can be reached from more than one.
 */
export function pageTrail(pathname: string, search: Record<string, unknown>): Step[] {
  const view = activeView(pathname)
  if (view === null) return []

  const page = LINKED_PAGES.find((linked) => linked.segment === pageSegment(pathname))
  if (page === undefined) return [{ kind: 'view', view }]

  // The view the link came from, and the page's own `under` when it carried none — the same default
  // the sidebar uses, read from the same entry so the two cannot say different things.
  const trail: Step[] = [{ kind: 'view', view: asView(search.from) ?? page.under }]

  if (page.segment === 'commit') {
    const sha = typeof search.sha === 'string' ? search.sha : ''
    // A URL naming no commit is the route's own not-found; the trail stops at the view rather than
    // drawing a crumb for a commit there is none of.
    if (sha !== '') trail.push({ kind: 'commit', sha })
    return trail
  }

  // The commit only where one linked here: a file opened from the tree has no commit above it, and
  // naming the one that last touched it would claim a route the reader did not take.
  if (typeof search.fromCommit === 'string' && search.fromCommit !== '')
    trail.push({ kind: 'commit', sha: search.fromCommit })
  const path = typeof search.path === 'string' ? search.path : ''
  if (path !== '') trail.push({ kind: 'file', path })
  return trail
}

/**
 * The project-relative segment of a path, or null for a path that is not inside a project at all.
 * Both questions this module answers start here — which item is lit, and which page this is — so
 * the path is read once. Two readings of it drifted the moment one of them wanted `/projects/new`
 * excluded and the other did not.
 */
function pageSegment(pathname: string): string | null {
  // A trailing slash is the same page, and the router hands one out for an index route.
  const path = pathname.replace(/\/$/, '')

  // `/projects/new` is slug-shaped and is not a project: it is the form that makes one, and a static
  // segment beats `$project` in the router for the same reason.
  if (path === '/projects/new') return null

  const inProject = /^\/projects\/[^/]+(?:\/(.*))?$/.exec(path)
  if (inProject === null) return null

  // Matched with nothing after the slug is the project's own page, which is the overview and is an
  // empty segment — not the same answer as "this path is not in a project", which is null above.
  return inProject[1] ?? ''
}

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
  const segment = pageSegment(pathname)
  if (segment === null) return null

  return VIEW_SEGMENTS.get(segment) ?? VIEW_ALIASES.get(segment) ?? null
}
