import type { QueryClient } from '@tanstack/react-query'
import { Outlet, createRootRouteWithContext } from '@tanstack/react-router'
import { NotFound } from '@/components/NotFound'
import { RootLayout } from '@/app/RootLayout'
import { bareFirstPage } from '@/lib/urls/firstPage'

// The error and not-found components render through this route's own Outlet, so they are already
// inside RootLayout and must not wrap themselves in a second one.
export const Route = createRootRouteWithContext<{ queryClient: QueryClient }>()({
  component: () => (
    <RootLayout>
      <Outlet />
    </RootLayout>
  ),
  notFoundComponent: NotFound,
  // On the root, so every route a link can lead to runs it: a paged route added later leaves page 1
  // out of its URL without having to remember to, and a route with no page has nothing to strip.
  search: { middlewares: [bareFirstPage()] },
})
