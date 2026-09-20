import type { QueryClient } from '@tanstack/react-query'
import { Outlet, createRootRouteWithContext } from '@tanstack/react-router'
import { NotFound } from '@/components/NotFound'
import { RootLayout } from '@/app/RootLayout'
import { RouteError } from '@/components/RouteError'

// The error and not-found components render through this route's own Outlet, so they are already
// inside RootLayout and must not wrap themselves in a second one.
export const Route = createRootRouteWithContext<{ queryClient: QueryClient }>()({
  component: () => (
    <RootLayout>
      <Outlet />
    </RootLayout>
  ),
  errorComponent: RouteError,
  notFoundComponent: NotFound,
})
