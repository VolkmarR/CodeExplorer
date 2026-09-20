import { useMutation, useQueryClient } from '@tanstack/react-query'
import { useState } from 'react'
import { createProject } from '@/features/projects/api'
import { ErrorPanel } from '@/components/ErrorPanel'
import { Button } from '@/components/ui/button'
import { Card, CardContent } from '@/components/ui/card'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { projectsQuery } from '@/features/projects/queries'

/** Both callbacks leave the form; they are separate so a caller can tell a create from a dismissal. */
type NewProjectFormProps = { onCancel: () => void; onCreated: () => void }

/** Creating a project: the slug agents will address it by, and a display name that can change. */
export function NewProjectForm({ onCancel, onCreated }: NewProjectFormProps) {
  const queryClient = useQueryClient()
  const [slug, setSlug] = useState('')
  const [name, setName] = useState('')
  const [single, setSingle] = useState(false)

  const create = useMutation({
    mutationFn: () => createProject(slug.trim(), name.trim(), single),
    onSuccess: async () => {
      setSlug('')
      setName('')
      setSingle(false)
      await queryClient.invalidateQueries(projectsQuery())
      onCreated()
    },
  })

  return (
    <Card>
      {/* No card title: the page this sits on is already headed "New project". */}
      <CardContent>
        <form
          onSubmit={(event) => {
            event.preventDefault()
            create.mutate()
          }}
          className="space-y-4"
        >
          <div className="grid gap-4 sm:grid-cols-2">
            <div className="space-y-2">
              <Label htmlFor="project-slug">Slug</Label>
              <Input
                id="project-slug"
                value={slug}
                onChange={(event) => setSlug(event.target.value)}
                placeholder="acme-platform"
                className="font-mono"
                required
              />
              <p className="text-xs text-muted-foreground">
                Lower-case letters, digits and hyphens. It is the endpoint URL agents keep, so it
                cannot change later.
              </p>
            </div>
            <div className="space-y-2">
              <Label htmlFor="project-name">Display name</Label>
              <Input
                id="project-name"
                value={name}
                onChange={(event) => setName(event.target.value)}
                placeholder="Acme Platform"
                required
              />
              <p className="text-xs text-muted-foreground">Free text, and free to change.</p>
            </div>
          </div>
          {/* The one decision on this form that cannot be revisited, so it says so where it is made
              rather than in a confirmation afterwards. */}
          <div className="rounded-lg border border-dashed p-3">
            <Label className="flex items-start gap-3 font-normal">
              <input
                type="checkbox"
                checked={single}
                onChange={(event) => setSingle(event.target.checked)}
                className="mt-0.5 size-4 accent-primary"
              />
              <span className="space-y-1">
                <span className="block text-sm font-medium">Single repository project</span>
                <span className="block text-xs text-muted-foreground">
                  Files are named by their path alone —{' '}
                  <code className="font-mono">src/Widget.cs</code> rather than{' '}
                  <code className="font-mono">repo/src/Widget.cs</code>. The project then holds one
                  repository and cannot take a second, and this cannot be changed later, because
                  either change would rename every file agents have been quoting.
                </span>
              </span>
            </Label>
          </div>
          {create.error ? <ErrorPanel error={create.error} /> : null}
          <div className="flex gap-2">
            <Button type="submit" disabled={create.isPending}>
              {create.isPending ? 'Creating…' : 'Create project'}
            </Button>
            <Button type="button" variant="ghost" onClick={onCancel} disabled={create.isPending}>
              Cancel
            </Button>
          </div>
        </form>
      </CardContent>
    </Card>
  )
}
