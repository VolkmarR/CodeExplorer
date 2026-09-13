import { Link } from '@tanstack/react-router'

/**
 * The frame every page sits in. It takes its content as children rather than rendering an `Outlet`
 * itself, so the root route's error and not-found components can reuse it outside the matched tree.
 */
export function RootLayout({ children }: { children: React.ReactNode }) {
  return (
    <div className="min-h-dvh">
      <header className="border-b">
        <div className="mx-auto flex max-w-6xl items-center gap-3 px-6 py-4">
          <Link to="/" className="text-lg font-semibold tracking-tight">
            CodeExplorer
          </Link>
          <span className="text-sm text-muted-foreground">operator</span>
        </div>
      </header>
      <main className="mx-auto max-w-6xl px-6 py-8">{children}</main>
    </div>
  )
}
