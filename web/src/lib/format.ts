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
