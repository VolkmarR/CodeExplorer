import { useLocation, useParams, useRouterState } from '@tanstack/react-router'
import { AppSidebar } from '@/app/AppSidebar'
import { TopBar } from '@/app/TopBar'
import { activeView, pageTrail } from '@/app/navigation'
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

  // The path is the whole of what lights an item: every view has a route of its own, and a file or a
  // commit belongs to the view its route sits under however it was opened.
  //
  // The trail is the other question — how the reader got here — and only the URL's own search params
  // can answer it, because the same file route is reached from the tree, a search result and a
  // commit. Read loosely for the reason the project param is: most routes carry neither param.
  const { pathname, search } = useLocation()
  const view = activeView(pathname)
  const steps = pageTrail(pathname, search)

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
          <TopBar project={project} steps={steps} loading={loading} />
          <main className={cn('w-full flex-1 px-4 py-5', wide ? '' : 'mx-auto max-w-6xl')}>
            {children}
          </main>
        </SidebarInset>
      </SidebarProvider>
    </TooltipProvider>
  )
}
