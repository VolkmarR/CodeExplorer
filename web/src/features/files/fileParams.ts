import { asView, type Origin } from '@/components/appNavigation'

/**
 * What the file view needs from the URL: which file, which line to land on, and where the reader
 * came from. The origin is carried rather than derived, because the route is the same whether the
 * file was opened from the tree, a search result or a commit, and the trail above it differs.
 */
export interface FileParameters {
  path: string
  line?: number
  /** The view that linked here; absent reads as Files, which is what every link meant before. */
  from?: Origin['view']
  /** The commit that linked here, where one did, so the trail can name it between view and file. */
  fromCommit?: string
}

export function validateFileSearch(search: Record<string, unknown>): FileParameters {
  const line = Number(search.line)
  return {
    from: asView(search.from),
    fromCommit:
      typeof search.fromCommit === 'string' && search.fromCommit !== ''
        ? search.fromCommit
        : undefined,
    line: Number.isInteger(line) && line > 0 ? line : undefined,
    path: typeof search.path === 'string' ? search.path : '',
  }
}

/**
 * The URL of one file, optionally one line of it, and where it was opened from. Five places link to
 * a file — a search result and its line numbers, a listing, a churn row, a commit's file list, the
 * code view's own line anchors — and each of them used to spell the object out.
 */
export function fileSearch(path: string, line?: number, origin?: Origin): FileParameters {
  return { from: origin?.view, fromCommit: origin?.commit, line, path }
}
