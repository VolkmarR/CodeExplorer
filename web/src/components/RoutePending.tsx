import { Skeleton } from '@/components/ui/skeleton'

/**
 * What a route shows while its loader is still out. One shape for every page — a heading, a line
 * under it, then rows — because the pages here all open with a title and a list, and a skeleton that
 * guessed each page's exact layout would be wrong the first time a page changed.
 */
export function RoutePending() {
  return (
    <div className="space-y-6" aria-busy="true" aria-label="Loading">
      <div className="space-y-2">
        <Skeleton className="h-7 w-48" />
        <Skeleton className="h-4 w-80 max-w-full" />
      </div>
      <div className="space-y-3">
        <Skeleton className="h-16 w-full" />
        <Skeleton className="h-16 w-full" />
        <Skeleton className="h-16 w-full" />
      </div>
    </div>
  )
}
