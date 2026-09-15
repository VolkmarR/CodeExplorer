import { useSuspenseQuery } from '@tanstack/react-query'
import { useNavigate } from '@tanstack/react-router'
import { useState } from 'react'
import { projectQuery } from '@/features/projects/queries'
import type { SearchParameters } from '@/features/search/searchParams'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { RepositorySelect } from '@/features/projects/RepositorySelect'

/** Every repository: the select's value for "no scope", since Base UI wants a value and not undefined. */
const ALL = ''

/**
 * Edits the search in the URL. The inputs are local only until submit, because navigating on every
 * keystroke would put a history entry behind each letter and run a query for each prefix.
 *
 * The search endpoint has no repository parameter — a repository is the first segment of a path, so
 * scoping to one is the glob `slug/*`. The form offers it as a select all the same, because picking a
 * repository is the common narrowing and typing its slug with a star after it is not how anyone would
 * guess to do that. A glob the operator typed wins over the select: the two are the same field.
 */
export function SearchForm({ project, search }: { project: string; search: SearchParameters }) {
  const navigate = useNavigate()
  const { data: detail } = useSuspenseQuery(projectQuery(project))
  const [draft, setDraft] = useState(search)

  // The draft is seeded from the URL, and the URL can change under it: the pager navigates, and the
  // back button rewinds to another query. Adjusting state during render — React's own answer to a
  // prop the state derives from — re-seeds it then, where a plain `useState(search)` would show the
  // previous query's text in the box for the results now on screen. The router hands out a new
  // object per navigation, so identity is the right comparison.
  const [seeded, setSeeded] = useState(search)
  if (seeded !== search) {
    setSeeded(search)
    setDraft(search)
  }

  // Which repository the path glob names, if it is exactly the shape the select writes.
  const scoped = detail.repositories.find((r) => `${r.slug}/*` === draft.path)?.slug ?? ALL
  const multiRepository = !detail.singleRepository && detail.repositories.length > 1

  return (
    <form
      className="space-y-4 rounded-lg border bg-card p-4"
      onSubmit={(event) => {
        event.preventDefault()
        // Back to page one: the page the last query was on says nothing about this one.
        void navigate({
          params: { project },
          search: { ...draft, page: 1 },
          to: '/projects/$project/search',
        })
      }}
    >
      <div className="flex flex-wrap gap-3">
        <div className="min-w-64 flex-1 space-y-2">
          <Label htmlFor="search-query">Query</Label>
          <div className="relative">
            <Input
              id="search-query"
              value={draft.q}
              onChange={(event) => setDraft({ ...draft, q: event.target.value })}
              placeholder="IndexBuilder"
              className="pr-32 font-mono"
              // The page is for typing a query; it should not take a click to start. The rule guards
              // against focus stolen from content a reader was in, and this page has none before the box.
              // oxlint-disable-next-line jsx-a11y/no-autofocus, react-doctor/no-autofocus
              autoFocus
            />
            {/* The two ways a query is read, as a switch inside the box rather than a checkbox below
                it, because which one is on changes what the text in the box means. */}
            <fieldset className="absolute inset-y-1 right-1 flex rounded-sm border text-xs">
              <legend className="sr-only">Query mode</legend>
              <ModeButton on={!draft.regex} onClick={() => setDraft({ ...draft, regex: false })}>
                Text
              </ModeButton>
              <ModeButton on={draft.regex} onClick={() => setDraft({ ...draft, regex: true })}>
                Regex
              </ModeButton>
            </fieldset>
          </div>
        </div>
        <div className="w-32 space-y-2">
          <Label htmlFor="search-extension">Extension</Label>
          <Input
            id="search-extension"
            value={draft.extension ?? ''}
            onChange={(event) => setDraft({ ...draft, extension: event.target.value || undefined })}
            placeholder="cs"
            className="font-mono"
          />
        </div>
        {multiRepository ? (
          <div className="w-52 space-y-2">
            <Label htmlFor="search-repository">Repository</Label>
            <RepositorySelect
              id="search-repository"
              repositories={detail.repositories}
              value={scoped}
              onChange={(slug) =>
                setDraft({ ...draft, path: slug === ALL ? undefined : `${slug}/*` })
              }
            />
          </div>
        ) : null}
        <div className="w-56 space-y-2">
          <Label htmlFor="search-path">Path glob</Label>
          <Input
            id="search-path"
            value={draft.path ?? ''}
            onChange={(event) => setDraft({ ...draft, path: event.target.value || undefined })}
            placeholder="*/src/*"
            className="font-mono"
          />
        </div>
      </div>

      <div className="flex flex-wrap items-center gap-6">
        <label className="flex items-center gap-2 text-sm" htmlFor="search-case">
          <input
            id="search-case"
            name="caseSensitive"
            type="checkbox"
            checked={draft.caseSensitive}
            onChange={(event) => setDraft({ ...draft, caseSensitive: event.target.checked })}
            className="size-4 accent-primary"
          />
          Case sensitive
        </label>
        <p className="text-xs text-muted-foreground">
          {draft.regex
            ? 'RE2 syntax: no lookaround, no backreferences.'
            : 'Text matches whole identifiers; switch to Regex for a pattern.'}
        </p>
        <Button type="submit" className="ml-auto">
          Search
        </Button>
      </div>
    </form>
  )
}

function ModeButton({
  on,
  onClick,
  children,
}: {
  on: boolean
  onClick: () => void
  children: React.ReactNode
}) {
  return (
    <button
      type="button"
      aria-pressed={on}
      onClick={onClick}
      className="px-2 font-sans text-muted-foreground first:rounded-l-[3px] last:rounded-r-[3px] hover:text-foreground aria-pressed:bg-muted aria-pressed:text-foreground"
    >
      {children}
    </button>
  )
}
