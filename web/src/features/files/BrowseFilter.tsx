import { useSuspenseQuery } from '@tanstack/react-query'
import { useNavigate } from '@tanstack/react-router'
import { useDraft } from '@/hooks/useDraft'
import { globSearch, type BrowseParameters } from '@/lib/urls/browseParams'
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
  const [draft, setDraft] = useDraft(search)

  // One repository has nothing to choose between; the tree's root already is it (ADR-0006).
  const multiRepository = !detail.singleRepository && detail.repositories.length > 1

  return (
    <form
      className="flex flex-wrap items-end gap-3 rounded-lg border bg-card p-4"
      onSubmit={(event) => {
        event.preventDefault()
        // Through the builder, which starts at page 1: the page a filter is submitted from belongs
        // to the previous glob, and page 7 of a new match is a blank listing that reads as no
        // matches at all.
        void navigate({
          params: { project },
          search: globSearch(draft.glob, draft.repository, draft.path),
          to: '/projects/$project/files',
        })
      }}
    >
      {/* `flex flex-col gap-2` and not `space-y-2`, which is the same stack until one of them holds
          a select. `space-y` puts the margin on every child but the last, and Base UI's select
          renders a hidden input after its trigger — so the trigger counted as "not last" and kept a
          trailing 8px that `items-end` then aligned the whole row against. The select rode 8px above
          the input and the button, and the two labels missed each other by the same 8px. `gap` only
          spaces flow siblings, and a fixed-position input is not one. */}
      <div className="flex min-w-64 flex-1 flex-col gap-2">
        <Label htmlFor="browse-glob">Path glob</Label>
        <Input
          id="browse-glob"
          name="glob"
          value={draft.glob}
          onChange={(event) => setDraft({ ...draft, glob: event.target.value })}
          placeholder="*Handler.cs — empty to browse the tree"
          font="mono"
        />
      </div>
      {multiRepository ? (
        <div className="flex w-52 flex-col gap-2">
          <Label htmlFor="browse-repository">Repository</Label>
          <RepositorySelect
            id="browse-repository"
            repositories={detail.repositories}
            value={draft.repository ?? ''}
            onChange={(slug) => setDraft(globSearch(draft.glob, slug, draft.path))}
          />
        </div>
      ) : null}
      <Button type="submit">Filter</Button>
    </form>
  )
}
