import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { RouterProvider, createRouter } from '@tanstack/react-router'
import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import { RoutePending } from '@/components/RoutePending'
import { Toaster } from '@/components/ui/toast'
import { routeTree } from './routeTree.gen'
import './styles.css'

// Retries are off for the same reason as in the API client: the server's failures are decisions
// about the request, not transient faults.
const queryClient = new QueryClient({
  defaultOptions: { queries: { retry: false, staleTime: 30_000 } },
})

// The client rides in the router context so a loader can prime a query before the route renders, and
// `defaultPreload: 'intent'` does that on hover. Every read here is one GET against a local index.
//
// The pending component is set once here rather than per route, so every page waits the same way.
// The router's default holds the old page for a full second before showing it, which on a local
// index means the skeleton almost never appears and a slow search looks like a dead click; 200ms is
// short enough to answer the click and long enough that a cache hit never flashes it. Once shown it
// stays for 300ms so a fast answer does not blink.
const router = createRouter({
  context: { queryClient },
  defaultPendingComponent: RoutePending,
  defaultPendingMinMs: 300,
  defaultPendingMs: 200,
  defaultPreload: 'intent',
  routeTree,
})

declare module '@tanstack/react-router' {
  interface Register {
    router: typeof router
  }
}

createRoot(document.querySelector('#root')!).render(
  <StrictMode>
    <QueryClientProvider client={queryClient}>
      <Toaster>
        <RouterProvider router={router} />
      </Toaster>
    </QueryClientProvider>
  </StrictMode>,
)
