import { createFileRoute } from '@tanstack/react-router'
import { OverviewSectionPage } from '@/features/projects/OverviewSectionPage'
import { ensureOverview } from '@/features/projects/queries'
import { validateOverviewSearch } from '@/lib/urls/overviewParams'

export const Route = createFileRoute('/projects/$project/activity')({
  component: () => <OverviewSectionPage page="activity" />,
  loaderDeps: ({ search }) => search,
  loader: ({ context, deps, params }) => ensureOverview(context.queryClient, params.project, deps),
  validateSearch: validateOverviewSearch,
})
