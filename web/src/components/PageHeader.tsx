/**
 * A page's heading: its `h1`, one clause on what the page answers from, and its own controls. The
 * one place the page heading's size, weight and tracking are set, so a page inside `PageCard` and
 * a page with no card frame carry the same heading.
 *
 * `children` sit beside the hint, before the actions; `PageCard` puts its badges there.
 */
export function PageHeader({
  title,
  hint,
  actions,
  children,
}: {
  title: React.ReactNode
  /** One clause on what this view answers from. Beside the title, not under it. */
  hint?: React.ReactNode
  actions?: React.ReactNode
  children?: React.ReactNode
}) {
  return (
    <div className="flex flex-wrap items-center gap-x-3 gap-y-2">
      <h1 className="text-lg font-semibold tracking-tight">{title}</h1>
      {hint ? <span className="text-sm text-muted-foreground">{hint}</span> : null}
      {children}
      {actions ? <div className="ml-auto flex items-center gap-2">{actions}</div> : null}
    </div>
  )
}
