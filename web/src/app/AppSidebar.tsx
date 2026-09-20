import { useQuery } from '@tanstack/react-query'
import { Link } from '@tanstack/react-router'
import { Boxes, MoreHorizontal, Plus, RefreshCw, Settings, SquareCode } from 'lucide-react'
import { PROJECT_VIEWS } from '@/app/navigation'
import type { View } from '@/lib/urls/views'
import { indexState, type IndexState } from '@/features/projects/indexState'
import { StateDot } from '@/features/projects/StateDot'
import { projectQuery } from '@/features/projects/queries'
import { isRefreshRunning, refreshStatusQuery } from '@/features/refresh/queries'
import { useRefreshProject } from '@/features/refresh/useRefreshProject'
import { useRefreshWatcher } from '@/features/refresh/useRefreshWatcher'
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuTrigger,
} from '@/components/ui/dropdown-menu'
import { Separator } from '@/components/ui/separator'
import { toast } from '@/components/ui/toast'
import {
  Sidebar,
  SidebarContent,
  SidebarFooter,
  SidebarGroup,
  SidebarGroupContent,
  SidebarGroupLabel,
  SidebarHeader,
  SidebarMenu,
  SidebarMenuButton,
  SidebarMenuItem,
} from '@/components/ui/sidebar'
import { formatTime } from '@/lib/format'

/**
 * The only navigation there is: what exists globally, the project in hand and its views, and how
 * fresh the answers under them are.
 *
 * Every read here is a `useQuery` rather than the loader-and-`useSuspenseQuery` pair the routes
 * use, for the reason `AccountBar` gives: the sidebar renders in the frame that also wraps the
 * router's error and not-found components, so one that suspended or threw would take down the page
 * already reporting a failure. A frame with no project block is a fine frame.
 */
export function AppSidebar({ project, view }: { project: string | undefined; view: View | null }) {
  // Read once here and passed down, because the two places that show it — the project's line and
  // the foot — sit in different parts of the frame and would otherwise each subscribe and each
  // re-derive. The request is shared either way; the renders are not.
  const index = useProjectIndex(project)

  return (
    <Sidebar collapsible="icon">
      <SidebarHeader>
        <SidebarMenu>
          <SidebarMenuItem>
            <SidebarMenuButton
              size="lg"
              tooltip="CodeExplorer"
              render={
                <Link to="/">
                  <SquareCode className="text-primary" />
                  {/* Hidden rather than truncated in the rail: a name cut to one letter is not a
                      shorter name, and the mark beside it is already the app. */}
                  <span className="text-base font-semibold tracking-tight group-data-[collapsible=icon]:hidden">
                    CodeExplorer
                  </span>
                </Link>
              }
            />
          </SidebarMenuItem>
        </SidebarMenu>
      </SidebarHeader>

      <SidebarContent>
        <SidebarGroup>
          <SidebarGroupContent>
            <SidebarMenu>
              <SidebarMenuItem>
                <SidebarMenuButton
                  tooltip="Projects"
                  render={
                    <Link to="/">
                      <Boxes />
                      <span>Projects</span>
                    </Link>
                  }
                />
              </SidebarMenuItem>
              <SidebarMenuItem>
                <SidebarMenuButton
                  tooltip="New project"
                  render={
                    <Link to="/projects/new">
                      <Plus />
                      <span>New project</span>
                    </Link>
                  }
                />
              </SidebarMenuItem>
            </SidebarMenu>
          </SidebarGroupContent>
        </SidebarGroup>

        {project ? <ProjectBlock project={project} view={view} index={index} /> : null}
      </SidebarContent>

      <SidebarFooter className="gap-1.5 text-xs group-data-[collapsible=icon]:hidden">
        {project ? <IndexFreshness index={index} /> : null}
        <span className="px-2 text-muted-foreground/70">Version {APP_VERSION}</span>
      </SidebarFooter>
    </Sidebar>
  )
}

/**
 * What the frame needs to know about the project's index: what to call it, whether a rebuild is in
 * flight, and when it was last built. `project` may be absent — the list and the new-project page
 * are outside any project — so the queries are disabled rather than the hook being conditional.
 */
interface ProjectIndex {
  state: IndexState
  running: boolean
  builtAt: string | null
  phase: string | undefined
}

function useProjectIndex(project: string | undefined): ProjectIndex {
  const enabled = project !== undefined
  const { data: detail } = useQuery({ ...projectQuery(project ?? ''), enabled })
  const { data: status } = useQuery({ ...refreshStatusQuery(project ?? '', 'frame'), enabled })
  const builtAt = detail?.index.builtAt ?? null

  // The poll above is the only one mounted on every view of a project, so it is also the one thing
  // that can notice a refresh finishing whatever page is open — and a finished refresh is what makes
  // every answer under the project stale. The frame watches; the invalidation itself is the refresh
  // feature's (`useRefreshWatcher`).
  useRefreshWatcher(project, status)

  return {
    builtAt,
    phase: status?.phase,
    running: isRefreshRunning(status),
    state: indexState(builtAt),
  }
}

/**
 * The project in hand, with its own menu, and its views. The views come from one table rather than
 * six near-identical blocks, so adding one is an entry beside its name and its matcher rather than
 * a block to copy here and a label to remember to add over there.
 */
function ProjectBlock({
  project,
  view,
  index,
}: {
  project: string
  view: View | null
  index: ProjectIndex
}) {
  const refresh = useRefreshProject(project)
  const { running, state } = index

  return (
    <SidebarGroup>
      <SidebarGroupLabel className="gap-2" title={`Index ${state}`}>
        <StateDot state={running ? 'refreshing' : state} />
        <span className="flex-1 truncate font-mono text-foreground">{project}</span>
        <DropdownMenu>
          <DropdownMenuTrigger
            className="rounded-sm p-0.5 text-muted-foreground hover:text-foreground"
            aria-label={`Actions for the ${project} project`}
          >
            <MoreHorizontal className="size-4" />
          </DropdownMenuTrigger>
          <DropdownMenuContent align="start">
            {/* The one thing worth doing to a project from wherever you happen to be: every view
                under this menu answers from the index, and this is what makes the index current.
                A refusal — another refresh running, too little disk — is the server's own prose,
                and a menu has no panel to put it in, so it goes to a toast. */}
            <DropdownMenuItem
              disabled={running || refresh.isPending}
              onClick={() =>
                refresh.mutate(undefined, {
                  onError: (error) =>
                    toast.add({
                      description: error.message,
                      title: 'Not refreshing',
                      type: 'error',
                    }),
                })
              }
            >
              <RefreshCw />
              {running ? 'Refreshing…' : 'Refresh now'}
            </DropdownMenuItem>
            <DropdownMenuItem
              render={
                <Link to="/projects/$project/settings" params={{ project }}>
                  <Settings />
                  Settings and repositories
                </Link>
              }
            />
            {/* Deleting the project stays on the settings page, where it asks first: a menu item
                that removes an index a slip of the hand away is not a menu item. */}
          </DropdownMenuContent>
        </DropdownMenu>
      </SidebarGroupLabel>
      <SidebarGroupContent>
        <SidebarMenu>
          {PROJECT_VIEWS.map(({ view: item, Icon, label, link }) => (
            <SidebarMenuItem key={item}>
              <SidebarMenuButton
                isActive={view === item}
                tooltip={label}
                render={
                  // `exact`, because which item is lit is decided once from the path above and a
                  // link's own match would be a second answer to the same question. The router
                  // matches a prefix by default, so Overview — whose path is every other view's
                  // first segments — reports itself as the page you are on while you stand on
                  // Settings, and a reader is told twice where they are, once wrongly.
                  <Link {...link} params={{ project }} activeOptions={{ exact: true }}>
                    <Icon />
                    <span>{label}</span>
                  </Link>
                }
              />
            </SidebarMenuItem>
          ))}
        </SidebarMenu>
      </SidebarGroupContent>
    </SidebarGroup>
  )
}

/**
 * The foot of the sidebar: how fresh the answers are, and what is building them if anything is.
 * Every view above it answers from the index, so this is the one caveat that applies to all of them.
 */
function IndexFreshness({ index }: { index: ProjectIndex }) {
  const { builtAt, phase, running, state } = index

  return (
    <>
      <span className="flex items-center gap-2 px-2">
        <StateDot state={running ? 'refreshing' : state} />
        <span className="truncate text-muted-foreground">
          {running
            ? `Refreshing · ${phase}`
            : `Index ${state}${builtAt ? ` · ${formatTime(builtAt)}` : ''}`}
        </span>
      </span>
      <Separator />
    </>
  )
}
