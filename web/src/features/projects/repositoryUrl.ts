/** The hosts the add-repository form has instructions for; `other` is any git server it does not know. */
export type RepositoryProvider = 'local' | 'azure-devops' | 'github' | 'gitlab' | 'other'

/** What the add-repository form reads from a URL while it is typed, to say what this host needs. */
export interface RepositoryUrlShape {
  provider: RepositoryProvider
  /** ssh or scp-style: a token in the credential field is sent as a password, which these hosts refuse over ssh. */
  ssh: boolean
  /** The user name before the host of an http(s) URL, which the server refuses. */
  userName: string | null
  /** The password or token before the host of an http(s) URL, which belongs in the credential field. */
  secret: string | null
  /** The URL with its user part removed, the form the server accepts; null when there is none. */
  withoutUserPart: string | null
}

// The local shapes the server classifies as local: a drive path, a UNC path, a file URL, a path from
// the root or relative to the working directory. A loopback host is local to the server too, but it is
// still a git server over the network, and may want a credential, so it stays `other`.
const LOCAL_PATH = /^(?:[A-Za-z]:[\\/]|\\\\|file:|\.{0,2}[\\/])/i
// scp-style `git@host:org/repo`, as the server's own pattern reads it: no scheme, a host, a colon.
const SCP_SYNTAX = /^(?:[^@:/\\]+@)?(?<host>[^@:/\\]+):(?!\/\/)/

/**
 * The provider, transport and user part of a pasted URL, or null while the field is empty. It reads
 * the host only: the server decides what is accepted, and this exists to say why, before it is asked.
 */
export function repositoryUrlShape(url: string): RepositoryUrlShape | null {
  const trimmed = url.trim()
  if (trimmed === '') return null
  const shape: RepositoryUrlShape = {
    provider: 'other',
    ssh: false,
    userName: null,
    secret: null,
    withoutUserPart: null,
  }
  if (LOCAL_PATH.test(trimmed)) return { ...shape, provider: 'local' }

  if (!trimmed.includes('://')) {
    const host = SCP_SYNTAX.exec(trimmed)?.groups?.host
    return host === undefined ? shape : { ...shape, provider: providerOf(host), ssh: true }
  }

  let parsed: URL
  try {
    parsed = new URL(trimmed)
  } catch {
    return shape
  }
  const scheme = parsed.protocol.slice(0, -1).toLowerCase()
  const provider = providerOf(parsed.hostname)
  if (scheme !== 'http' && scheme !== 'https') return { ...shape, provider, ssh: scheme === 'ssh' }
  if (parsed.username === '' && parsed.password === '') return { ...shape, provider }
  return {
    ...shape,
    provider,
    userName: parsed.username === '' ? null : decodeURIComponent(parsed.username),
    secret: parsed.password === '' ? null : decodeURIComponent(parsed.password),
    // Cut from the text as typed rather than rebuilt by `URL`, which would re-encode the path.
    withoutUserPart: trimmed.replace(/^(https?:\/\/)[^/]*@/i, '$1'),
  }
}

/**
 * An Azure DevOps URL without the organization its clone dialog copies in as a user name, and the name
 * removed; null for any other URL. Only a bare user name: a password is a secret, and is moved to the
 * credential field by the reader, not dropped where nobody sees it.
 */
export function withoutAzureDevOpsUserName(url: string): { url: string; userName: string } | null {
  const shape = repositoryUrlShape(url)
  if (shape?.provider !== 'azure-devops' || shape.secret !== null) return null
  if (shape.userName === null || shape.withoutUserPart === null) return null
  return { url: shape.withoutUserPart, userName: shape.userName }
}

function providerOf(host: string): RepositoryProvider {
  const name = host.toLowerCase().replace(/\.$/, '')
  if (
    name === 'dev.azure.com' ||
    name === 'ssh.dev.azure.com' ||
    name.endsWith('.visualstudio.com')
  ) {
    return 'azure-devops'
  }
  if (name === 'github.com' || name === 'www.github.com' || name === 'ssh.github.com')
    return 'github'
  // A self-hosted GitLab is conventionally gitlab.<company>, and needs the same token as gitlab.com.
  if (name === 'gitlab.com' || name.startsWith('gitlab.')) return 'gitlab'
  return 'other'
}

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
