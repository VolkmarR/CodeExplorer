import { useSuspenseQuery } from '@tanstack/react-query'
import { useNavigate } from '@tanstack/react-router'
import { useState } from 'react'
import type { BrowseParameters } from '@/features/files/browseParams'
import { projectQuery } from '@/features/projects/queries'
import { RepositorySelect } from '@/features/projects/RepositorySelect'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'

/**
 * Narrows the listing, into the URL, so a filtered view of a project's files can be linked. An empty
 * glob is not a narrower filter but the other view: the tree, one level at a time.
 */
export function BrowseFilter({ project, search }: { project: string; search: BrowseParameters }) {
  const navigate = useNavigate()
  const { data: detail } = useSuspenseQuery(projectQuery(project))
  const [draft, setDraft] = useState(search)

  // Re-seeded when the URL changes under the form, for the reason SearchForm gives at length.
  const [seeded, setSeeded] = useState(search)
  if (seeded !== search) {
    setSeeded(search)
    setDraft(search)
  }

  // One repository has nothing to choose between; the tree's root already is it (ADR-0006).
  const multiRepository = !detail.singleRepository && detail.repositories.length > 1

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
          placeholder="*Handler.cs — empty to browse the tree"
          className="font-mono"
        />
      </div>
      {multiRepository ? (
        <div className="w-52 space-y-2">
          <Label htmlFor="browse-repository">Repository</Label>
          <RepositorySelect
            id="browse-repository"
            repositories={detail.repositories}
            value={draft.repository ?? ''}
            onChange={(slug) => setDraft({ ...draft, repository: slug || undefined })}
          />
        </div>
      ) : null}
      <Button type="submit">Filter</Button>
    </form>
  )
}
