/**
 * A git URL down to what tells two apart at a glance: the host and the last path segment. The scheme
 * and the organisation in between are the same for every repository of one operator, and the full
 * URL is one hover away in the title.
 */
export function shortUrl(url: string): string {
  try {
    const parsed = new URL(url)
    // A local path (`C:\repos\x`) parses too, as a scheme with no host, and would shorten to `/…/x`.
    if (parsed.host === '') return url
    const last = parsed.pathname.split('/').findLast(Boolean) ?? ''
    return `${parsed.host}/…/${last}`
  } catch {
    // Not a URL the browser parses — an scp-style `git@host:org/repo.git` — so it shows as written.
    return url
  }
}
