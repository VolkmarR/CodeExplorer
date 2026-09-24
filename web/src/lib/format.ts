export function formatBytes(size: number): string {
  if (size < 1024) return `${size} B`
  if (size < 1024 * 1024) return `${(size / 1024).toFixed(1)} KiB`
  return `${(size / 1024 / 1024).toFixed(1)} MiB`
}

export function formatCount(count: number): string {
  return count.toLocaleString()
}

/** A share from 0 to 1 as a whole percentage. */
export function formatPercent(share: number): string {
  return `${Math.round(share * 100)}%`
}

/** Absolute rather than relative: an operator asking when a project was last built wants the date. */
export function formatTime(value: string | null): string {
  if (!value) return 'never'
  return new Date(value).toLocaleString()
}

/**
 * The day only, for a commit: when a line was last changed is a question about years and months, and
 * the time of day is noise next to a name and a subject.
 */
export function formatDate(value: string): string {
  return new Date(value).toLocaleDateString()
}

/** Seven characters, what git itself abbreviates to and what a reader compares against a commit list. */
export function shortSha(sha: string): string {
  return sha.slice(0, 7)
}

/**
 * The last segment of a qualified path. Three views name a file by its own name and dim the
 * directory before it — search results, the file page's title, the file rail — and each of them
 * used to cut the string itself.
 */
export function fileName(qualifiedPath: string): string {
  return qualifiedPath.slice(qualifiedPath.lastIndexOf('/') + 1)
}

/** Where that last segment starts, for the callers that dim everything before it. */
export function directoryOf(qualifiedPath: string): string {
  return qualifiedPath.slice(0, qualifiedPath.lastIndexOf('/') + 1)
}

/**
 * A file name split at its extension. The rule is the part worth having in one place: a dotfile
 * such as `.gitignore` has no extension, and neither does `Dockerfile`, which the whole name names
 * — so a leading dot starts a stem and never an extension. Two callers ask the same question for
 * different reasons, the highlighter to pick a language and the file rail to guess what a file
 * probably declares, and they must not answer it differently.
 */
export function splitFileName(qualifiedPath: string): { stem: string; extension: string } {
  const name = fileName(qualifiedPath)
  const dot = name.lastIndexOf('.')
  return dot > 0
    ? { extension: name.slice(dot + 1), stem: name.slice(0, dot) }
    : { extension: '', stem: name }
}
