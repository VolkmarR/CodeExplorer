import { Link } from '@tanstack/react-router'
import { Fragment } from 'react'
import { VIEW_NAMES, type View } from '@/components/appNavigation'
import { AccountBar } from '@/features/auth/AccountBar'
import { McpEndpoint } from '@/features/projects/McpEndpoint'
import { projectSearch } from '@/features/projects/projectParams'
import { ThemeToggle } from '@/components/ThemeToggle'
import {
  Breadcrumb,
  BreadcrumbItem,
  BreadcrumbLink,
  BreadcrumbList,
  BreadcrumbPage,
  BreadcrumbSeparator,
} from '@/components/ui/breadcrumb'
import { Separator } from '@/components/ui/separator'
import { SidebarTrigger } from '@/components/ui/sidebar'

interface Crumb {
  label: string
  /** Absent on the crumb that is where you are, which is never a link. */
  link?: React.ReactElement
  mono?: boolean
}

/** The trail from the project list down to the view on screen, as deep as the page actually is. */
function crumbs(project: string | undefined, view: View | null): Crumb[] {
  const trail: Crumb[] = [{ label: 'Projects', link: <Link to="/">Projects</Link> }]
  if (!project) return trail

  trail.push({
    label: project,
    link: (
      <Link to="/projects/$project" params={{ project }} search={projectSearch('overview')}>
        {project}
      </Link>
    ),
    mono: true,
  })
  // A page inside a project that is not one of its views — the router's own not-found — stops here
  // rather than naming a view it is not on.
  if (view) trail.push({ label: VIEW_NAMES[view] })
  return trail
}

/**
 * Where you are, and what you can do from here. Nothing else: the views moved to the sidebar
 * precisely so this row carries the path and the endpoint rather than competing with them.
 */
export function TopBar({
  project,
  view,
  loading,
}: {
  project: string | undefined
  view: View | null
  loading: boolean
}) {
  return (
    <header className="sticky top-0 z-20 flex h-14 shrink-0 items-center gap-2 border-b bg-background px-4">
      <SidebarTrigger />
      <Separator orientation="vertical" className="mr-1 h-4" />
      <Breadcrumb>
        <BreadcrumbList>
          {/* Built as a list and mapped, so where the trail ends is decided once: the last crumb is
              where you are and is never a link, whether that is the project list, a project with no
              view matched, or a view. Written as branches, each of those was its own case. */}
          {crumbs(project, view).map((crumb, position, all) => (
            <Fragment key={crumb.label}>
              {position > 0 ? <BreadcrumbSeparator /> : null}
              <BreadcrumbItem>
                {position === all.length - 1 || !crumb.link ? (
                  <BreadcrumbPage className={crumb.mono ? 'font-mono' : undefined}>
                    {crumb.label}
                  </BreadcrumbPage>
                ) : (
                  <BreadcrumbLink
                    className={crumb.mono ? 'font-mono' : undefined}
                    render={crumb.link}
                  />
                )}
              </BreadcrumbItem>
            </Fragment>
          ))}
        </BreadcrumbList>
      </Breadcrumb>

      <div className="ml-auto flex items-center gap-2">
        {project ? <McpEndpoint project={project} /> : null}
        <ThemeToggle />
        <AccountBar />
      </div>

      {/* Decorative: the pending component below already announces the wait with `aria-busy`, so
          this is hidden from readers rather than made a second progress announcement. */}
      {loading ? (
        <div aria-hidden="true" className="absolute inset-x-0 -bottom-px h-0.5 overflow-hidden">
          <div className="h-full w-1/3 bg-primary motion-safe:animate-[slide_1s_ease-in-out_infinite]" />
        </div>
      ) : null}
    </header>
  )
}
