import { ErrorPanel } from '@/components/ErrorPanel'

/**
 * What every route renders when its loader throws. A named function rather than an inline arrow in
 * each route file, so the four routes cannot drift into four slightly different failure screens.
 */
export function RouteError({ error }: { error: unknown }) {
  return <ErrorPanel error={error} />
}
