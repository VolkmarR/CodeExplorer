import { createFileRoute } from '@tanstack/react-router'
import { ToolCallsPage } from '@/features/toolCalls/ToolCallsPage'
import { toolCallsQuery } from '@/features/toolCalls/queries'

export const Route = createFileRoute('/projects/$project/tool-calls')({
  component: ToolCallsPage,
  loader: ({ context, params }) =>
    context.queryClient.ensureQueryData(toolCallsQuery(params.project)),
})
