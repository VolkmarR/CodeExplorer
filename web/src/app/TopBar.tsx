import { Link } from '@tanstack/react-router'
import { Fragment } from 'react'
import { PROJECT_VIEWS, VIEW_NAMES, type Step } from '@/app/navigation'
import { commitSearch } from '@/lib/urls/commitParams'
import { AccountBar } from '@/features/auth/AccountBar'
import { McpEndpoint } from '@/features/projects/McpEndpoint'
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
import { fileName, shortSha } from '@/lib/format'

interface Crumb {
  label: string
  /** Absent on the crumb that is where you are, which is never a link. */
  link?: React.ReactElement
  mono?: boolean
}

/**
 * The trail from the project list down to the page on screen, as deep as the page actually is. The
 * steps below the project come from the URL's own account of how it was reached (`pageTrail`), so a
 * file opened from a commit reads `… › History › <sha> › <file>` and the same file opened from the
 * tree reads `… › Files › <file>`.
 */
function crumbs(project: string | undefined, steps: Step[]): Crumb[] {
  const trail: Crumb[] = [{ label: 'Projects', link: <Link to="/">Projects</Link> }]
  if (!project) return trail

  trail.push({
    label: project,
    link: (
      <Link to="/projects/$project" params={{ project }}>
        {project}
      </Link>
    ),
    mono: true,
  })
  // A page inside a project that is not one of its views — the router's own not-found — stops here
  // rather than naming a view it is not on, which is what an empty trail means.
  for (const step of steps) trail.push(stepCrumb(project, step))
  return trail
}

/**
 * One step, rendered. Every step that can be one is drawn as a link and the list below decides which
 * is the page: the last crumb is never a link, so a step does not have to know where it sits.
 */
function stepCrumb(project: string, step: Step): Crumb {
  if (step.kind === 'view') {
    // Asserted, not guarded: `Step`'s view is a `View`, and `PROJECT_VIEWS` is where that union is
    // spelled — a miss here would mean the table and its own type had come apart.
    const item = PROJECT_VIEWS.find((view) => view.view === step.view)!
    return {
      label: VIEW_NAMES[step.view],
      link: (
        <Link {...item.link} params={{ project }}>
          {VIEW_NAMES[step.view]}
        </Link>
      ),
    }
  }

  if (step.kind === 'commit') {
    return {
      label: shortSha(step.sha),
      link: (
        <Link to="/projects/$project/commit" params={{ project }} search={commitSearch(step.sha)}>
          {shortSha(step.sha)}
        </Link>
      ),
      mono: true,
    }
  }

  // The file's own name and not its qualified path: the path is already on the page, under the
  // title, and a trail carrying it would be longer than the row it sits in.
  return { label: fileName(step.path), mono: true }
}

/**
 * Where you are, and what you can do from here. Nothing else: the views moved to the sidebar
 * precisely so this row carries the path and the endpoint rather than competing with them.
 */
export function TopBar({
  project,
  steps,
  loading,
}: {
  project: string | undefined
  /** How this page was reached, below the project. Empty for a page that is on no view at all. */
  steps: Step[]
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
          {crumbs(project, steps).map((crumb, position, all) => (
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
          <div className="h-full w-1/3 bg-primary motion-safe:animate-slide" />
        </div>
      ) : null}
    </header>
  )
}
