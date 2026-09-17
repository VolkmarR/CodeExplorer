import { Link } from '@tanstack/react-router'
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
          <BreadcrumbItem>
            {project ? (
              <BreadcrumbLink render={<Link to="/">Projects</Link>} />
            ) : (
              <BreadcrumbPage>Projects</BreadcrumbPage>
            )}
          </BreadcrumbItem>
          {project ? (
            <>
              <BreadcrumbSeparator />
              <BreadcrumbItem>
                <BreadcrumbLink
                  className="font-mono"
                  render={
                    <Link
                      to="/projects/$project"
                      params={{ project }}
                      search={projectSearch('overview')}
                    >
                      {project}
                    </Link>
                  }
                />
              </BreadcrumbItem>
            </>
          ) : null}
          {project && view ? (
            <>
              <BreadcrumbSeparator />
              <BreadcrumbItem>
                <BreadcrumbPage>{VIEW_NAMES[view]}</BreadcrumbPage>
              </BreadcrumbItem>
            </>
          ) : null}
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
