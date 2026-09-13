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
