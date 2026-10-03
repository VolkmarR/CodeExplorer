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

export function fetchSearch(
  project: string,
  { caseSensitive, page, q, regex, extension, path }: SearchParameters,
) {
  // Listed rather than spread so the query string keeps the order it has always been sent in.
  return http
    .get(`projects/${project}/search`, {
      searchParams: { caseSensitive, page, q, regex, extension, path },
    })
    .json<GrepResult>()
}
