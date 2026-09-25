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

/**
 * Calendar days the server cut in UTC, `yyyy-mm-dd`, such as the start of a chart's bar. Read and
 * formatted in UTC, so a day is the same day wherever the browser is.
 */
function utcDay(day: string): Date {
  return new Date(`${day}T00:00:00Z`)
}

const utcFormats = {
  date: new Intl.DateTimeFormat(undefined, { dateStyle: 'medium', timeZone: 'UTC' }),
  day: new Intl.DateTimeFormat(undefined, { day: 'numeric', month: 'short', timeZone: 'UTC' }),
  month: new Intl.DateTimeFormat(undefined, { month: 'short', timeZone: 'UTC', year: 'numeric' }),
  monthName: new Intl.DateTimeFormat(undefined, { month: 'short', timeZone: 'UTC' }),
}

/** A UTC day in full, as a bar's tooltip names it. */
export function formatUtcDate(day: string): string {
  return utcFormats.date.format(utcDay(day))
}

/** A UTC day without its year, for an axis whose years are read elsewhere. */
export function formatUtcDay(day: string): string {
  return utcFormats.day.format(utcDay(day))
}

/** The month a UTC day is in, with its year. */
export function formatUtcMonth(day: string): string {
  return utcFormats.month.format(utcDay(day))
}

/** The name of the month a UTC day is in, for an axis. */
export function formatUtcMonthName(day: string): string {
  return utcFormats.monthName.format(utcDay(day))
}

/** Whether a UTC day is a Monday, and its day of the month and month (0 to 11), for choosing ticks. */
export function utcDayParts(day: string): { monday: boolean; date: number; month: number } {
  const date = utcDay(day)
  return { date: date.getUTCDate(), monday: date.getUTCDay() === 1, month: date.getUTCMonth() }
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
