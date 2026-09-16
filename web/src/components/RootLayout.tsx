import { Link, useLocation, useParams, useRouterState } from '@tanstack/react-router'
import { ChevronRight } from 'lucide-react'
import { AccountBar } from '@/features/auth/AccountBar'
import { treeSearch } from '@/features/files/browseParams'
import { cn } from '@/lib/utils'

/** Same treatment for every nav link; the lit one adds the second class. */
const NAV_LINK = 'rounded-md px-2 py-1 text-sm text-muted-foreground hover:text-foreground'
const NAV_LINK_ACTIVE = 'bg-muted text-foreground'

/**
 * The frame every page sits in, and the only navigation there is: where you are, and the three views
 * of a project. It takes its content as children rather than rendering an `Outlet` itself, so the root
 * route's error and not-found components can reuse it outside the matched tree.
 */
export function RootLayout({ children }: { children: React.ReactNode }) {
  // Not every page is inside a project — the list and the new-project page are not — so the param is
  // read loosely and the project half of the bar simply does not render when there is none.
  const { project } = useParams({ strict: false })

  // Which tab is lit is decided here rather than by `activeProps`, because reading a file is part of
  // Files and lives at its own route (`/file`, not under `/files`) — a link's own active state cannot
  // know that, and would leave the reader of a file standing on no tab at all.
  const { pathname } = useLocation()
  const view = pathname.endsWith('/search')
    ? 'search'
    : pathname.endsWith('/history')
      ? 'history'
      : pathname.endsWith('/files') || pathname.endsWith('/file')
        ? 'files'
        : 'settings'

  // Code and search results are wide; the project list and settings are prose and forms. The reading
  // views drop the column so a long line of code is not folded at 1152px on a wide screen.
  const wide = view === 'files' || view === 'search'

  // The bar is the one place a pending navigation shows before the pending component takes over,
  // so a click on a tab answers within a frame rather than after the loader's delay.
  const loading = useRouterState({ select: (state) => state.isLoading })

  return (
    <div className="min-h-dvh">
      <header className="relative border-b">
        <div className="mx-auto flex max-w-(--breakpoint-2xl) flex-wrap items-center gap-2 px-6 py-3">
          <Link to="/" className="flex items-center gap-2 text-lg font-semibold tracking-tight">
            <span
              aria-hidden="true"
              className="size-4 rounded-sm bg-linear-to-br from-primary to-primary/50"
            />
            CodeExplorer
          </Link>
          {project ? (
            <>
              <ChevronRight className="size-4 text-muted-foreground/60" />
              <span className="font-mono text-sm">{project}</span>
              <nav className="ml-4 flex items-center gap-1">
                <Link
                  to="/projects/$project/files"
                  params={{ project }}
                  search={treeSearch()}
                  className={cn(NAV_LINK, view === 'files' && NAV_LINK_ACTIVE)}
                >
                  Files
                </Link>
                <Link
                  to="/projects/$project/search"
                  params={{ project }}
                  search={{ caseSensitive: false, page: 1, q: '', regex: false }}
                  className={cn(NAV_LINK, view === 'search' && NAV_LINK_ACTIVE)}
                >
                  Search
                </Link>
                <Link
                  to="/projects/$project/history"
                  params={{ project }}
                  search={{ page: 1 }}
                  className={cn(NAV_LINK, view === 'history' && NAV_LINK_ACTIVE)}
                >
                  History
                </Link>
                <Link
                  to="/projects/$project"
                  params={{ project }}
                  className={cn(NAV_LINK, view === 'settings' && NAV_LINK_ACTIVE)}
                >
                  Settings
                </Link>
              </nav>
            </>
          ) : null}
          <AccountBar />
        </div>
        {/* Decorative: the pending component below already announces the wait with `aria-busy`, so
            this is hidden from readers rather than made a second progress announcement. */}
        {loading ? (
          <div aria-hidden="true" className="absolute inset-x-0 -bottom-px h-0.5 overflow-hidden">
            <div className="h-full w-1/3 bg-primary motion-safe:animate-[slide_1s_ease-in-out_infinite]" />
          </div>
        ) : null}
      </header>
      <main className={cn('mx-auto px-6 py-8', wide ? 'max-w-(--breakpoint-2xl)' : 'max-w-6xl')}>
        {children}
      </main>
    </div>
  )
}
