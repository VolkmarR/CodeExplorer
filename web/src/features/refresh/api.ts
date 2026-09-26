import { http } from '@/lib/http'

/** What a refresh reports while it runs and when it ends. Each shape mirrors a record in the C# host. */

export interface IndexSummary {
  repositories: number
  files: number
  lines: number
  skipped: string[]
  /** Choices the refresh made for a repository it did read, such as the branch a detached remote is followed on. */
  notes: string[]
}

/** The states the server names, serialised as words rather than ordinals so this union is stable. */
export type RefreshState = 'NeverRun' | 'Queued' | 'Running' | 'Succeeded' | 'Failed'

/**
 * How far a running refresh has got. `step` of `totalSteps` is always there; `done` and `total`
 * only for the steps that can count their work, and `total` alone may be null for a step that knows
 * how far it has got but not how far it is going — the commit walk.
 */
export interface RefreshProgress {
  step: number
  totalSteps: number
  phase: string
  done: number | null
  total: number | null
}

/** What one phase of a refresh cost. `step` is the step it ran at; a step can report several. */
export interface PhaseCost {
  step: number
  phase: string
  seconds: number
}

/**
 * Where a refresh stands. It is in-memory on the server, so a replica that scaled to zero comes back
 * saying `NeverRun` even for a project with an index; when that index was built is on the project.
 */
export interface RefreshStatus {
  project: string
  state: RefreshState
  phase: string
  startedAt: string | null
  finishedAt: string | null
  summary: IndexSummary | null
  error: string | null
  /** Set while the refresh runs; null before it starts and after it ends. */
  progress: RefreshProgress | null
  /**
   * What each phase cost, oldest first, and the only part of a refresh that outlives it: `phase`
   * says what is happening now and is gone the moment it changes. Empty before the first phase ends.
   */
  phases: PhaseCost[]
}

export function startRefresh(project: string) {
  return http.post(`projects/${project}/refresh`).json<RefreshStatus>()
}

export function fetchRefreshStatus(project: string) {
  return http.get(`projects/${project}/refresh`).json<RefreshStatus>()
}
