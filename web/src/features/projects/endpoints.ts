/**
 * Where an agent connects to a project. It is composed here rather than fetched, because the server
 * mounts it under the project group it already routes (`/projects/{project}/mcp`, `Program.cs`) and
 * the browser is already at the origin that serves it — an endpoint asked for over the API would be
 * the same string with a round trip in front of it.
 *
 * The origin is the page's own, which in development is the Vite server rather than the host. That
 * address is not the one an agent connects to — `vp dev` proxies `/api` and nothing else — but it
 * is still what this browser is looking at, and guessing at the host's public name from here would
 * be worse than saying where you are.
 */
export function mcpEndpoint(origin: string, project: string) {
  return `${origin.replace(/\/$/, '')}/projects/${project}/mcp`
}
