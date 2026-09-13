# One MCP endpoint per project

Each project is served at its own remote MCP URL (`/projects/{project}/mcp`), and the server binds
the project from the path at connection time. The alternative was a single endpoint at the root with
a required `project` argument on every tool.

A department normally works with exactly one project, so the path carries the information that would
otherwise ride on every call. It keeps the tool signatures smaller, removes a discovery round-trip,
and removes the failure mode where an agent names a project that does not exist — or names the wrong
one and silently searches someone else's codebase. A department needing two projects configures two
MCP servers; clients namespace tools per server, so nothing collides.

## Consequences

- There is no in-band way for an agent to discover which projects exist. The project list lives in
  the web UI and in operator configuration, not in MCP.
- Protected-resource metadata is path-scoped. The 401 from a project endpoint must carry
  `WWW-Authenticate: Bearer resource_metadata="…/.well-known/oauth-protected-resource/projects/{project}/mcp"`,
  and that document must be served per path. This is a protocol requirement for client discovery,
  independent of what the authorization rules are.
- Copilot Studio needs one custom connector per project, because the connector definition embeds the
  host and path. Claude Code and the Claude connector need only a URL each.
- **The endpoint boundary is organizational, not a security boundary.** Every project shares one
  Entra audience, so any authenticated user's token is valid against every project endpoint. For now
  that is accepted: the requirement is protection from unauthenticated users, nothing finer. Adding
  per-project authorization later means mapping Entra group claims to projects in configuration and
  enforcing it as a filter on the MCP route — deliberately deferred, not overlooked.
