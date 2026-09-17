import type { ProjectTab } from '@/features/projects/projectParams'

/** The sidebar's items, in the order it lists them. */
export type View = 'overview' | 'files' | 'search' | 'history' | 'churn' | 'settings'

/**
 * What each one is called, wherever something other than the sidebar has to name it — the
 * breadcrumb, so far. Beside `activeView` rather than in the bar that reads it, so that adding a
 * view puts its matcher and its name in the same edit and they cannot come to disagree.
 */
export const VIEW_NAMES: Record<View, string> = {
  churn: 'Churn',
  files: 'Files',
  history: 'History',
  overview: 'Overview',
  search: 'Search',
  settings: 'Settings',
}

/**
 * Which sidebar item is lit, decided from the path rather than by each link's `activeProps`,
 * because two of the six do not sit where their name suggests: reading a file is part of Files and
 * lives at its own route (`/file`, not under `/files`), and Overview and Settings are two items on
 * one route told apart by the tab in its URL. A link's own active state can know neither, and would
 * leave the reader of a file standing on no item at all.
 *
 * A module of its own rather than a function inside the sidebar: it is the one part of the frame
 * that grows with every view added, it needs none of the component's state to decide, and every
 * case it gets wrong is a case worth a test.
 */
export function activeView(pathname: string, tab: ProjectTab): View | null {
  // A trailing slash is the same page, and the router hands one out for an index route.
  const path = pathname.replace(/\/$/, '')

  if (path.endsWith('/search')) return 'search'
  if (path.endsWith('/history')) return 'history'
  if (path.endsWith('/churn')) return 'churn'
  if (path.endsWith('/files') || path.endsWith('/file')) return 'files'

  // `/projects/new` reaches here as a slug-shaped path and is not a project: it is the form that
  // makes one, and a static segment beats `$project` in the router for the same reason.
  if (path === '/projects/new') return null

  return /^\/projects\/[^/]+$/.test(path) ? tab : null
}
