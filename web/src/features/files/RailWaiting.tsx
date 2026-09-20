import { ErrorPanel } from '@/components/ErrorPanel'
import { RailPanel } from '@/features/files/RailPanel'
import { RailPending } from '@/features/files/RailPending'

/**
 * A panel with nothing to show yet, and the same panel when the request failed outright. Every panel
 * in the rail waits for its own request, so the wait and the failure look the same in all of them.
 */
export function RailWaiting({ title, error }: { title: string; error: unknown }) {
  return (
    <RailPanel title={title}>
      {error ? <ErrorPanel error={error} title={`${title} could not be read`} /> : <RailPending />}
    </RailPanel>
  )
}
