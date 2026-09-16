export function formatBytes(size: number): string {
  if (size < 1024) return `${size} B`
  if (size < 1024 * 1024) return `${(size / 1024).toFixed(1)} KiB`
  return `${(size / 1024 / 1024).toFixed(1)} MiB`
}

export function formatCount(count: number): string {
  return count.toLocaleString()
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
