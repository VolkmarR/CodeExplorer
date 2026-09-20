import { http } from '@/lib/http'

/**
 * Who is signed in: the one read that stayed in `lib/` when every other response shape moved to the
 * feature that reads it. It is not a feature's answer — it is the session the whole frame is drawn
 * inside, asked once on every view and owned by no concept in CONTEXT.md — and it belongs beside the
 * client for the same reason the 401 hook and the sign-in URL do: all three are this app's side of
 * one decision about who may call `/api` at all.
 */

/**
 * Whether this server has a tenant at all, and who is signed in to it. Two questions and not one: a
 * development server is deliberately unauthenticated (ADR-0004), so a UI reading only `signedIn`
 * would offer every developer a sign-in that goes nowhere.
 *
 * There is no token here, and that is the design rather than an omission: the browser holds a cookie
 * this server issued and never sees a token at all.
 */
export interface AuthStatus {
  enabled: boolean
  signedIn: boolean
  name: string | null
}

/** Anonymous, and mapped whether or not there is a tenant: it is what says which of those it is. */
export function fetchAuthStatus() {
  return http.get('auth/me').json<AuthStatus>()
}
