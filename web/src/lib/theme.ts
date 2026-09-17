/**
 * Which of the two themes the page wears, and where that choice is kept.
 *
 * Not a search param, and the one piece of state in this app that deliberately is not: how a reader
 * likes to look at a screen is about the reader and not about what is on it, so a link someone
 * sends must not carry it (CODING_STANDARDS, TypeScript). `localStorage` is therefore the store,
 * and it is read twice — once by the inline script in `index.html` before the first paint, once
 * here — which is why the shape of a stored value is decided in this file and asserted in tests.
 *
 * The document's own class is the source of truth for what is on screen, because the inline script
 * has already set it before React ran. This module reads it back rather than re-deciding it.
 */

export type ThemePreference = 'light' | 'dark' | 'system'
export type Theme = 'light' | 'dark'

/** The `localStorage` key. The inline script in `index.html` spells the same string. */
export const THEME_KEY = 'codeexplorer.theme'

const DARK_QUERY = '(prefers-color-scheme: dark)'
const PREFERENCES = new Set(['light', 'dark', 'system'])

/**
 * Makes sense of whatever was stored. Anything unrecognised is the system's choice rather than an
 * error: a value written by a later version of this app should degrade to the default, not throw
 * on a page that has not rendered yet.
 *
 * It takes the stored string rather than the store, so it is a pure function of one value and the
 * callers keep the `try`/`catch` that reading `localStorage` needs in a locked-down browser.
 */
export function readPreference(raw: string | null): ThemePreference {
  return raw !== null && PREFERENCES.has(raw) ? (raw as ThemePreference) : 'system'
}

/** The stored preference, or the default where storage is blocked or empty. */
function storedPreference(): ThemePreference {
  try {
    return readPreference(globalThis.localStorage.getItem(THEME_KEY))
  } catch {
    // Private window, or site data blocked. Following the system is the honest answer.
    return 'system'
  }
}

/** What to actually render. `prefersDark` is the media query's answer, passed in so this stays pure. */
export function resolveTheme(preference: ThemePreference, prefersDark: boolean): Theme {
  if (preference === 'system') return prefersDark ? 'dark' : 'light'
  return preference
}

/**
 * What one click on the toggle asks for: the theme that is not on screen, stated explicitly. There
 * is no way back to `system` from here, on purpose — a control with three states and one button
 * cannot say which of them it is in, and the browser's own setting is where "follow the system"
 * belongs.
 */
export function nextPreference(preference: ThemePreference, prefersDark: boolean): ThemePreference {
  return resolveTheme(preference, prefersDark) === 'dark' ? 'light' : 'dark'
}

/**
 * The theme on screen, read off the document. `useSyncExternalStore` in the toggle calls this, so
 * the browser globals are touched from a subscription rather than from a render — which is also
 * what keeps a reader who never chose a theme following their system when it changes at dusk.
 */
export function currentTheme(): Theme {
  return document.documentElement.classList.contains('dark') ? 'dark' : 'light'
}

/**
 * Who to tell when the theme changes. Two things move it — a click on the toggle and, for a reader
 * who has chosen neither theme, the system itself at dusk — and both go through here, so a
 * component reading `currentTheme` sees either.
 */
const listeners = new Set<() => void>()

export function subscribeToTheme(onChange: () => void) {
  const query = globalThis.matchMedia(DARK_QUERY)
  const handle = () => {
    // Only a reader following the system is moved by this; an explicit choice stands.
    if (storedPreference() === 'system') applyTheme(currentSystemTheme())
    onChange()
  }
  query.addEventListener('change', handle)
  listeners.add(onChange)
  return () => {
    query.removeEventListener('change', handle)
    listeners.delete(onChange)
  }
}

function currentSystemTheme(): Theme {
  return globalThis.matchMedia(DARK_QUERY).matches ? 'dark' : 'light'
}

/**
 * Puts the theme on the document. The class is the only thing `styles.css` keys off, and it is set
 * on `<html>` rather than a wrapper so that a portalled popup — a dropdown, a tooltip, the mobile
 * sidebar sheet — is inside it too.
 */
export function applyTheme(theme: Theme) {
  document.documentElement.classList.toggle('dark', theme === 'dark')
}

/** What the toggle does: decide, apply, and remember for the next visit. */
export function chooseNextTheme() {
  const prefersDark = globalThis.matchMedia(DARK_QUERY).matches
  const next = nextPreference(storedPreference(), prefersDark)
  applyTheme(resolveTheme(next, prefersDark))
  try {
    globalThis.localStorage.setItem(THEME_KEY, next)
  } catch {
    // Storage is full or blocked in a locked-down browser. The theme still changed for this page,
    // which is what the click asked for; only outliving the tab is lost.
  }
  for (const listener of listeners) listener()
}
