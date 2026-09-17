import { useQuery } from '@tanstack/react-query'
import { Link } from '@tanstack/react-router'
import {
  Activity,
  Boxes,
  Clock,
  FolderTree,
  MoreHorizontal,
  Plus,
  RefreshCw,
  Search,
  Settings,
  SquareCode,
} from 'lucide-react'
import type { View } from '@/components/appNavigation'
import { DEFAULT_CHURN_DAYS } from '@/features/churn/churnParams'
import { treeSearch } from '@/features/files/browseParams'
import { projectSearch } from '@/features/projects/projectParams'
import { projectQuery } from '@/features/projects/queries'
import { searchSearch } from '@/features/search/searchParams'
import { refreshStatusQuery } from '@/features/refresh/queries'
import { useRefreshProject } from '@/features/refresh/useRefreshProject'
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
 * How old an index may be before the frame calls it stale, in milliseconds. A day: these projects
 * are refreshed on a schedule of hours, so anything that has gone a whole day without one has had
 * a run fail or a schedule stop — which is exactly what an operator opening the app wants told.
 *
 * Read against the clock, which every window in this app deliberately is not (CONTEXT.md,
 * _Window_) — and the difference is the point. A window over commits must not use the clock,
 * because the newest commit is the end of what the index knows and today is not. When the index
 * was *built* is the other kind of fact: a wall-clock event, and how long ago it happened is a
 * wall-clock question. It is still not a claim that the index is wrong. The index cannot make that
 * claim, because everything it could compare itself against comes out of itself.
 */
const STALE_AFTER = 24 * 60 * 60 * 1000

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

        {project ? <ProjectViews project={project} view={view} /> : null}
      </SidebarContent>

      <SidebarFooter className="gap-1.5 text-xs group-data-[collapsible=icon]:hidden">
        {project ? <IndexFreshness project={project} /> : null}
        <span className="px-2 text-muted-foreground/70">Version {APP_VERSION}</span>
      </SidebarFooter>
    </Sidebar>
  )
}

/** The project in hand, with its own menu, and the six views of it. */
function ProjectViews({ project, view }: { project: string; view: View | null }) {
  const { data: status } = useQuery(refreshStatusQuery(project))
  const running = status?.state === 'Queued' || status?.state === 'Running'
  const refresh = useRefreshProject(project)

  return (
    <SidebarGroup>
      <SidebarGroupLabel className="gap-2">
        <IndexDot project={project} />
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
                <Link
                  to="/projects/$project"
                  params={{ project }}
                  search={projectSearch('settings')}
                >
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
          <SidebarMenuItem>
            <SidebarMenuButton
              isActive={view === 'overview'}
              tooltip="Overview"
              render={
                <Link
                  to="/projects/$project"
                  params={{ project }}
                  search={projectSearch('overview')}
                >
                  <Activity />
                  <span>Overview</span>
                </Link>
              }
            />
          </SidebarMenuItem>
          <SidebarMenuItem>
            <SidebarMenuButton
              isActive={view === 'files'}
              tooltip="Files"
              render={
                <Link to="/projects/$project/files" params={{ project }} search={treeSearch()}>
                  <FolderTree />
                  <span>Files</span>
                </Link>
              }
            />
          </SidebarMenuItem>
          <SidebarMenuItem>
            <SidebarMenuButton
              isActive={view === 'search'}
              tooltip="Search"
              render={
                <Link to="/projects/$project/search" params={{ project }} search={searchSearch()}>
                  <Search />
                  <span>Search</span>
                </Link>
              }
            />
          </SidebarMenuItem>
          <SidebarMenuItem>
            <SidebarMenuButton
              isActive={view === 'history'}
              tooltip="History"
              render={
                <Link to="/projects/$project/history" params={{ project }} search={{ page: 1 }}>
                  <Clock />
                  <span>History</span>
                </Link>
              }
            />
          </SidebarMenuItem>
          <SidebarMenuItem>
            <SidebarMenuButton
              isActive={view === 'churn'}
              tooltip="Churn"
              render={
                <Link
                  to="/projects/$project/churn"
                  params={{ project }}
                  search={{ days: DEFAULT_CHURN_DAYS }}
                >
                  <Activity />
                  <span>Churn</span>
                </Link>
              }
            />
          </SidebarMenuItem>
          <SidebarMenuItem>
            <SidebarMenuButton
              isActive={view === 'settings'}
              tooltip="Settings"
              render={
                <Link
                  to="/projects/$project"
                  params={{ project }}
                  search={projectSearch('settings')}
                >
                  <Settings />
                  <span>Settings</span>
                </Link>
              }
            />
          </SidebarMenuItem>
        </SidebarMenu>
      </SidebarGroupContent>
    </SidebarGroup>
  )
}

type IndexState = 'fresh' | 'stale' | 'not built'

function indexState(builtAt: string | null): IndexState {
  if (!builtAt) return 'not built'
  return Date.now() - new Date(builtAt).getTime() > STALE_AFTER ? 'stale' : 'fresh'
}

/**
 * The dot beside the project and in the foot. Decorative on purpose: colour is never the only
 * carrier here — the word is always beside it, and in the collapsed rail, where the word is gone,
 * the `title` on the group is what a reader is left with.
 */
const DOT: Record<IndexState | 'refreshing', string> = {
  fresh: 'bg-success',
  refreshing: 'animate-pulse bg-primary',
  stale: 'bg-warning',
  'not built': 'bg-muted-foreground/50',
}

function StateDot({ state }: { state: IndexState | 'refreshing' }) {
  return <span aria-hidden="true" className={`size-2 shrink-0 rounded-full ${DOT[state]}`} />
}

/** What the index is, in one word, beside the project it belongs to. */
function IndexDot({ project }: { project: string }) {
  const { data } = useQuery(projectQuery(project))
  const state = indexState(data?.index.builtAt ?? null)
  return (
    <span title={`Index ${state}`} className="flex items-center">
      <StateDot state={state} />
    </span>
  )
}

/**
 * The foot of the sidebar: how fresh the answers are, and what is building them if anything is.
 * Every view above it answers from the index, so this is the one caveat that applies to all of them.
 */
function IndexFreshness({ project }: { project: string }) {
  const { data: detail } = useQuery(projectQuery(project))
  const { data: status } = useQuery(refreshStatusQuery(project))
  const running = status?.state === 'Queued' || status?.state === 'Running'
  const state = indexState(detail?.index.builtAt ?? null)

  return (
    <>
      <span className="flex items-center gap-2 px-2">
        <StateDot state={running ? 'refreshing' : state} />
        <span className="truncate text-muted-foreground">
          {running
            ? `Refreshing · ${status.phase}`
            : `Index ${state}${detail?.index.builtAt ? ` · ${formatTime(detail.index.builtAt)}` : ''}`}
        </span>
      </span>
      <Separator />
    </>
  )
}
