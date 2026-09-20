import { Skeleton } from '@/components/ui/skeleton'

/**
 * A panel's content while its answer is on the way. Its own component and not a block inside one of
 * the panels because every panel in the rail waits for its own request, and a placeholder that
 * differs between them reads as two different kinds of waiting.
 */
export function RailPending() {
  return (
    <div className="space-y-2">
      <Skeleton className="h-4 w-full" />
      <Skeleton className="h-4 w-2/3" />
    </div>
  )
}
