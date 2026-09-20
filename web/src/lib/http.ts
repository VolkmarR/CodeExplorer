import ky, { HTTPError } from 'ky'

/**
 * The one client every feature calls `/api` through, and nothing about what it answers with: a
 * response shape belongs to the feature that reads it, in `features/<concept>/api.ts`. What is here
 * is what all of them share — the instance, the two hooks, the error they see, and the one helper
 * for a parameter more than one endpoint takes.
 */

/**
 * A failed request reaches the UI as ky's own `HTTPError`, re-exported under the name the components
 * use. `beforeError` below has already replaced its message with the server's prose, so a component
 * renders `error.message` and says nothing of its own.
 */
export { HTTPError as ApiError } from 'ky'

/**
 * Sign-in and sign-out are navigations and not calls, so they are URLs the browser goes to rather
 * than methods on a client. They have to be: the server answers each with a redirect to the tenant,
 * and only the address bar can follow one cross-origin.
 *
 * They are in this file rather than with the auth read beside it because the 401 hook below is the
 * first caller of `signInHref`, and `lib/` may not reach into a feature for it.
 */
export function signInHref(returnTo: string) {
  return `/api/auth/signin?returnUrl=${encodeURIComponent(returnTo)}`
}

/**
 * A form action and not an href: the server takes sign-out as a POST, so that a cross-site page
 * cannot force one and the cookie's SameSite=Lax is what stops it. Submitted, never linked.
 */
export const signOutAction = '/api/auth/signout'

export const http = ky.create({
  hooks: {
    afterResponse: [
      ({ request, response }) => {
        // The cookie expired while the page was open. The server refuses in prose rather than
        // redirecting, because a cross-origin 302 to the tenant would fail CORS and arrive here as a
        // network error — so the navigation that fixes it has to be made from this side.
        //
        // `/api/auth` is exempt: it is anonymous and never 401s, and a loop through the sign-in
        // endpoint is the one failure this hook could cause.
        if (response.status === 401 && !request.url.includes('/api/auth/')) {
          globalThis.location.assign(
            signInHref(globalThis.location.pathname + globalThis.location.search),
          )
        }
        return response
      },
    ],
    beforeError: [
      ({ error }) => {
        // A timeout or a dropped connection is not an HTTPError and has no body to read.
        if (!(error instanceof HTTPError)) return error

        // A semantic failure answers `{ error }` with prose naming what to try instead (an unknown
        // project, a pattern RE2 rejects, an index still to be built). ky's default message is the
        // status line, which would throw that away.
        //
        // Read `error.data`, never the response: ky parses the body into `data` before this hook
        // runs, which consumes it, so `response.clone()` here throws "Response body is already
        // used" and every server message becomes that TypeError instead.
        const body: unknown = error.data
        if (body && typeof body === 'object' && 'error' in body && typeof body.error === 'string') {
          error.message = body.error
        }
        return error
      },
    ],
  },
  prefix: '/api',
  // No retries: every failure this API produces is a decision it made about the request, not a
  // transient one, and repeating a rejected pattern only delays the explanation.
  retry: 0,
  // No request here waits on a rebuild any more — a refresh answers as soon as it is queued and the
  // progress is polled — but a search over a large project is still seconds rather than milliseconds.
  timeout: 30_000,
})

/**
 * Adds the repository to a request that has one, and leaves it off entirely when there is none:
 * the server reads a blank `repository` as a request to scope to one named "".
 *
 * Shared rather than per-feature because the mistake it avoids is the same one in every feature that
 * can narrow an answer to a repository, and three of them can.
 */
export function scoped(
  params: Record<string, string>,
  repository?: string,
): Record<string, string> {
  return repository ? { ...params, repository } : params
}
