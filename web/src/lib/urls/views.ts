/**
 * The views of a project, in the order the sidebar lists them. The names live here, beside the
 * parameter modules, rather than with the table that draws them: a view's name travels between pages
 * in the URL — `from=history` is what makes a file opened from a commit read as one — so it is a URL
 * contract that the frame happens to also draw, and not the other way round.
 *
 * `app/navigation.ts` holds the row each of these is drawn as, and what it links to.
 */
export const VIEWS = [
  'overview',
  'activity',
  'risk',
  'files',
  'search',
  'history',
  'churn',
  'settings',
] as const

export type View = (typeof VIEWS)[number]

/**
 * Where a page was reached from, carried in the URL by whatever linked there rather than guessed
 * from the path. A file opened from a commit is the same route as one opened from the tree and the
 * path cannot tell them apart, so the trail above it would otherwise have to lie about one of them.
 *
 * Both fields are optional and absent is the default: a pasted or hand-edited URL still opens, and
 * reads as though it had been reached from the view its route belongs to.
 */
export interface Origin {
  /** The view that linked here. */
  view?: View
  /** The commit that linked here, when the link came from one. */
  commit?: string
}

/** A view's name if that is what this is, and undefined for anything else a URL might carry. */
export function asView(value: unknown): View | undefined {
  return VIEWS.some((view) => view === value) ? (value as View) : undefined
}
