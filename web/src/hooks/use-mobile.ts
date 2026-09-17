import * as React from 'react'

const MOBILE_BREAKPOINT = 768

/**
 * One `MediaQueryList`, lazily made and kept. `getSnapshot` runs on every render and on every store
 * check, and each `matchMedia` call allocates another object.
 */
let query: MediaQueryList | undefined
function mobileQuery() {
  query ??= globalThis.matchMedia(`(max-width: ${MOBILE_BREAKPOINT - 1}px)`)
  return query
}

/**
 * Whether the sidebar should be a sheet rather than a column.
 *
 * Shadcn ships this as `useState` seeded from an effect, which sets state during the first effect
 * and so renders twice on every mount. `useSyncExternalStore` is what React offers for reading an
 * external source — the media query is one — and it reads on the first render instead.
 */
function subscribe(onChange: () => void) {
  const media = mobileQuery()
  media.addEventListener('change', onChange)
  return () => media.removeEventListener('change', onChange)
}

function getSnapshot() {
  return mobileQuery().matches
}

export function useIsMobile() {
  // The desktop layout is the snapshot for a render with no window: a sheet that cannot be opened
  // is worse than a column that is too wide.
  return React.useSyncExternalStore(subscribe, getSnapshot, () => false)
}
