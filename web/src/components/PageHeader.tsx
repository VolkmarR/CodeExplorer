/**
 * A page's heading: its `h1`, a short note on what the page is for, and its own controls. The one
 * place the page heading's size, weight and tracking are set, so a page inside `PageCard` and a
 * page with no card frame carry the same heading.
 */
export function PageHeader({
  title,
  hint,
  badges,
  actions,
}: {
  title: React.ReactNode
  /**
   * What this page answers from or is for. Beside the title; one long enough to miss the line
   * wraps under it.
   */
  hint?: React.ReactNode
  /** Beside the hint, before the actions. */
  badges?: React.ReactNode
  actions?: React.ReactNode
}) {
  return (
    <div className="flex flex-wrap items-center gap-x-3 gap-y-2">
      <h1 className="text-lg font-semibold tracking-tight">{title}</h1>
      {hint ? <span className="text-sm text-muted-foreground">{hint}</span> : null}
      {badges}
      {actions ? <div className="ml-auto flex items-center gap-2">{actions}</div> : null}
    </div>
  )
}
