import { useQuery } from '@tanstack/react-query'
import { useLocation } from '@tanstack/react-router'
import { LogIn, LogOut } from 'lucide-react'
import { signInHref, signOutAction } from '@/lib/api'
import { Avatar, AvatarFallback } from '@/components/ui/avatar'
import { authQuery } from './queries'

const ACTION =
  'flex items-center gap-1.5 rounded-md px-2 py-1 text-sm text-muted-foreground hover:text-foreground'

/**
 * Who is signed in, and the one thing to do about it. Read with `useQuery` rather than the
 * loader-and-`useSuspenseQuery` pair every route uses, and this is the exception that earns itself:
 * the bar renders in the frame that also wraps the router's error and not-found components, so a
 * header that suspended or threw would take down the page already reporting a failure. Absent is a
 * fine answer here — a frame with no account control is the frame this app had before #12.
 *
 * Neither control is a `Link`. Both leave the SPA — each ends at the tenant — so a client-side
 * navigation would simply not arrive. Sign-out is a form rather than the anchor it reads as, because
 * the server takes it as a POST: as a GET, anything that can put a URL on a page could sign the
 * operator out, a prefetching browser included.
 */
export function AccountBar() {
  const { data } = useQuery(authQuery())
  const { href } = useLocation()

  // Nothing to sign in to. A development server is deliberately unauthenticated (ADR-0004), and
  // offering a sign-in there would be offering a 404.
  if (!data?.enabled) return null

  if (!data.signedIn) {
    return (
      <a href={signInHref(href)} className={ACTION}>
        <LogIn className="size-4" />
        Sign in
      </a>
    )
  }

  return (
    <div className="flex items-center gap-2">
      <Avatar size="sm">
        {/* No image: the server answers with a name and nothing else, and it is the tenant rather
            than this app that holds a picture. So the fallback is the whole avatar, and it is
            there for the mark beside the name, not in place of it. */}
        <AvatarFallback aria-hidden="true">{initials(data.name)}</AvatarFallback>
      </Avatar>
      <span className="hidden text-sm text-muted-foreground sm:inline">{data.name}</span>
      <form method="post" action={signOutAction}>
        <button type="submit" className={ACTION}>
          <LogOut className="size-4" />
          <span className="sr-only sm:not-sr-only">Sign out</span>
        </button>
      </form>
    </div>
  )
}

/**
 * At most two letters from the name the tenant gave. It is a display name and not a structured
 * one — "Volkmar Rigo", "rigo.volkmar", a UPN — so this takes the first letter of the first two
 * words and gives up gracefully rather than pretending to parse a person.
 */
function initials(name: string | null): string {
  if (!name) return '?'
  const words = name.split(/[\s.@_-]+/).filter(Boolean)
  return words
    .slice(0, 2)
    .map((word) => word[0]?.toUpperCase() ?? '')
    .join('')
}
