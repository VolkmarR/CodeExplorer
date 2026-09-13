import { useNavigate } from '@tanstack/react-router'
import { useState } from 'react'
import type { BrowseParameters } from '@/features/files/browseParams'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'

/** Narrows the listing, into the URL, so a filtered view of a project's files can be linked. */
export function BrowseFilter({ project, search }: { project: string; search: BrowseParameters }) {
  const navigate = useNavigate()
  const [draft, setDraft] = useState(search)

  // Re-seeded when the URL changes under the form, for the reason SearchForm gives at length.
  const [seeded, setSeeded] = useState(search)
  if (seeded !== search) {
    setSeeded(search)
    setDraft(search)
  }

  return (
    <form
      className="flex flex-wrap items-end gap-3 rounded-lg border bg-card p-4"
      onSubmit={(event) => {
        event.preventDefault()
        void navigate({ params: { project }, search: draft, to: '/projects/$project/files' })
      }}
    >
      <div className="min-w-64 flex-1 space-y-2">
        <Label htmlFor="browse-glob">Path glob</Label>
        <Input
          id="browse-glob"
          name="glob"
          value={draft.glob}
          onChange={(event) => setDraft({ ...draft, glob: event.target.value })}
          placeholder="*.cs"
          className="font-mono"
        />
      </div>
      <div className="w-56 space-y-2">
        <Label htmlFor="browse-repository">Repository</Label>
        <Input
          id="browse-repository"
          name="repository"
          value={draft.repository ?? ''}
          onChange={(event) => setDraft({ ...draft, repository: event.target.value || undefined })}
          placeholder="all"
          className="font-mono"
        />
      </div>
      <Button type="submit">Filter</Button>
    </form>
  )
}
