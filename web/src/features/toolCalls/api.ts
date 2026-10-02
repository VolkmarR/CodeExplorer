import { http } from '@/lib/http'

/**
 * What agents asked a project over MCP, as `Infrastructure/ToolStatistics.cs` counts it. Held in the
 * server's memory and nowhere else, so every number is since `since`: a restart starts them over.
 */

export interface ToolTotals {
  tool: string
  calls: number
  failed: number
  /** Over the tool's last hundred calls, so a tool that got slow reads slow now. */
  p50Seconds: number
  p95Seconds: number
  maxSeconds: number
  lastCall: string
}

export interface MinuteCalls {
  minute: string
  calls: number
  failed: number
}

export interface RecentCall {
  /** The call's place in its project's count since the server started. */
  id: number
  at: string
  tool: string
  seconds: number
  failed: boolean
}

export interface ToolCallStatistics {
  since: string
  callsLastHour: number
  failedLastHour: number
  /** Over the project's last two hundred calls; null before the first one. */
  p95Seconds: number | null
  tools: ToolTotals[]
  /** The last sixty minutes, oldest first, empty minutes included. */
  perMinute: MinuteCalls[]
  /** Newest first. */
  recent: RecentCall[]
}

/** One project's line on the project list. Only projects an agent has called are present. */
export interface ProjectToolActivity {
  project: string
  callsLastHour: number
  lastCall: string
}

export function fetchToolCalls(slug: string) {
  return http.get(`projects/${slug}/tool-calls`).json<ToolCallStatistics>()
}

export function fetchToolActivity() {
  return http.get('tool-calls').json<ProjectToolActivity[]>()
}
