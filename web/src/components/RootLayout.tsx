import { useLocation, useParams, useRouterState, useSearch } from '@tanstack/react-router'
import { AppSidebar } from '@/components/AppSidebar'
import { TopBar } from '@/components/TopBar'
import { activeView } from '@/components/appNavigation'
import { validateProjectSearch } from '@/features/projects/projectParams'
import { SidebarInset, SidebarProvider } from '@/components/ui/sidebar'
import { TooltipProvider } from '@/components/ui/tooltip'
import { cn } from '@/lib/utils'

/**
 * The frame every page sits in: a sidebar that is the whole of the navigation, and a top bar that
 * says where you are and offers the endpoint, the theme and the account. It takes its content as
 * children rather than rendering an `Outlet` itself, so the root route's error and not-found
 * components can reuse it outside the matched tree.
 */
export function RootLayout({ children }: { children: React.ReactNode }) {
  // Not every page is inside a project — the list and the new-project page are not — so the param
  // is read loosely and the project half of the frame simply does not render when there is none.
  const { project } = useParams({ strict: false })

  // Loosely for the same reason, and because only one route has a `tab` at all: reading it strictly
  // would throw on the five that do not. It goes back through the route's own validator rather than
  // being re-read here, so the default lives in one place (`projectParams.ts`).
  const search: Record<string, unknown> = useSearch({ strict: false })
  const { pathname } = useLocation()
  const view = activeView(pathname, validateProjectSearch(search).tab)

  // Code and search results are wide; the overview, the settings and the forms are prose and
  // tables. The reading views drop the column so a long line of code is not folded on a wide screen.
  const wide = view === 'files' || view === 'search'

  // The bar is the one place a pending navigation shows before the pending component takes over,
  // so a click on a sidebar item answers within a frame rather than after the loader's delay.
  const loading = useRouterState({ select: (state) => state.isLoading })

  return (
    <TooltipProvider>
      <SidebarProvider>
        <AppSidebar project={project} view={view} />
        <SidebarInset className="min-w-0">
          <TopBar project={project} view={view} loading={loading} />
          <main className={cn('w-full flex-1 px-4 py-5', wide ? '' : 'mx-auto max-w-6xl')}>
            {children}
          </main>
        </SidebarInset>
      </SidebarProvider>
    </TooltipProvider>
  )
}
