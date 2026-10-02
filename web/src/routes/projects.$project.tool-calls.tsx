import { createFileRoute } from '@tanstack/react-router'
import { RouteError } from '@/components/RouteError'
import { ToolCallsPage } from '@/features/toolCalls/ToolCallsPage'
import { toolCallsQuery } from '@/features/toolCalls/queries'

export const Route = createFileRoute('/projects/$project/tool-calls')({
  component: ToolCallsPage,
  errorComponent: RouteError,
  loader: ({ context, params }) =>
    context.queryClient.ensureQueryData(toolCallsQuery(params.project)),
})
