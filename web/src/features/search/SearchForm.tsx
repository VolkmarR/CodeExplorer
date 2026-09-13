import { useNavigate } from '@tanstack/react-router'
import { useState } from 'react'
import type { SearchParameters } from '@/features/search/searchParams'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'

/**
 * Edits the search in the URL. The inputs are local only until submit, because navigating on every
 * keystroke would put a history entry behind each letter and run a query for each prefix.
 */
export function SearchForm({ project, search }: { project: string; search: SearchParameters }) {
  const navigate = useNavigate()
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
          <Input
            id="search-query"
            value={draft.q}
            onChange={(event) => setDraft({ ...draft, q: event.target.value })}
            placeholder="IndexBuilder"
            className="font-mono"
          />
        </div>
        <div className="w-40 space-y-2">
          <Label htmlFor="search-extension">Extension</Label>
          <Input
            id="search-extension"
            value={draft.extension ?? ''}
            onChange={(event) => setDraft({ ...draft, extension: event.target.value || undefined })}
            placeholder="cs"
            className="font-mono"
          />
        </div>
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
        <label className="flex items-center gap-2 text-sm" htmlFor="search-regex">
          <input
            id="search-regex"
            name="regex"
            type="checkbox"
            checked={draft.regex}
            onChange={(event) => setDraft({ ...draft, regex: event.target.checked })}
            className="size-4 accent-primary"
          />
          RE2 regular expression
        </label>
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
        <Button type="submit" className="ml-auto">
          Search
        </Button>
      </div>
    </form>
  )
}
