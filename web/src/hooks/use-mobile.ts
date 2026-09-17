import * as React from 'react'

const MOBILE_BREAKPOINT = 768
const QUERY = `(max-width: ${MOBILE_BREAKPOINT - 1}px)`

/**
 * Whether the sidebar should be a sheet rather than a column.
 *
 * Shadcn ships this as `useState` seeded from an effect, which sets state during the first effect
 * and so renders twice on every mount. `useSyncExternalStore` is what React offers for reading an
 * external source — the media query is one — and it reads on the first render instead.
 */
function subscribe(onChange: () => void) {
  const query = globalThis.matchMedia(QUERY)
  query.addEventListener('change', onChange)
  return () => query.removeEventListener('change', onChange)
}

function getSnapshot() {
  return globalThis.matchMedia(QUERY).matches
}

export function useIsMobile() {
  // The desktop layout is the snapshot for a render with no window: a sheet that cannot be opened
  // is worse than a column that is too wide.
  return React.useSyncExternalStore(subscribe, getSnapshot, () => false)
}
