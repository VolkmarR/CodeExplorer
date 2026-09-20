import type { SearchParameters } from '@/lib/urls/searchParams'
import { http } from '@/lib/http'

/** What a search answers with, and the one call that asks. Each shape mirrors a record in the C# host. */

export interface GrepLine {
  lineNumber: number
  text: string
  isMatch: boolean
}

export interface GrepFile {
  qualifiedPath: string
  matchCount: number
  matchesShown: number
  lines: GrepLine[]
}

export interface GrepResult {
  engine: string
  totalFiles: number
  totalLines: number
  page: number
  pageSize: number
  files: GrepFile[]
  filesMatchingWithoutFilters: number | null
}

export function fetchSearch(project: string, query: SearchParameters) {
  const searchParams: Record<string, string> = {
    caseSensitive: String(query.caseSensitive),
    page: String(query.page),
    q: query.q,
    regex: String(query.regex),
  }
  if (query.extension) searchParams.extension = query.extension
  if (query.path) searchParams.path = query.path
  return http.get(`projects/${project}/search`, { searchParams }).json<GrepResult>()
}
