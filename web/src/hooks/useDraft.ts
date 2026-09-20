import { useState } from 'react'

/**
 * A form's working copy of something that lives in the URL, re-seeded whenever the URL changes under
 * it.
 *
 * Both forms in this app edit search params: the inputs are local until submit, because navigating
 * on every keystroke would put a history entry behind each letter and run a query for each prefix.
 * That makes the URL a value the state derives from, and the URL can change while the form is on
 * screen — the pager navigates, the back button rewinds to another query. Adjusting state during
 * render is React's own answer to that, and without it a plain `useState(current)` shows the
 * previous query's text in the box beside the results now on screen.
 *
 * Identity is the right comparison because the router hands out a new object per navigation; a form
 * that passed a fresh object every render would re-seed itself and never keep a keystroke.
 */
export function useDraft<T>(current: T): [T, (draft: T) => void] {
  const [draft, setDraft] = useState(current)
  const [seeded, setSeeded] = useState(current)

  if (seeded !== current) {
    setSeeded(current)
    setDraft(current)
  }

  return [draft, setDraft]
}
