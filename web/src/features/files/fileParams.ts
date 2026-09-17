/** What the file view needs from the URL: which file, and which line to land on. */
export interface FileParameters {
  path: string
  line?: number
}

export function validateFileSearch(search: Record<string, unknown>): FileParameters {
  const line = Number(search.line)
  return {
    line: Number.isInteger(line) && line > 0 ? line : undefined,
    path: typeof search.path === 'string' ? search.path : '',
  }
}

/**
 * The URL of one file, and optionally one line of it. Five places link to a file — a search result
 * and its line numbers, a listing, a churn row, a commit's file list, the code view's own line
 * anchors — and each of them used to spell the object out.
 */
export function fileSearch(path: string, line?: number): FileParameters {
  return { line, path }
}
