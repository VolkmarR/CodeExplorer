import { ErrorPanel } from '@/components/ErrorPanel'

/**
 * What every route renders when its loader throws: the router's `defaultErrorComponent`, set once
 * in `main.tsx`, so no route can drift into a slightly different failure screen.
 */
export function RouteError({ error }: { error: unknown }) {
  return <ErrorPanel error={error} />
}
