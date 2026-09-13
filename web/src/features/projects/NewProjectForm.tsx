import { useMutation, useQueryClient } from '@tanstack/react-query'
import { useState } from 'react'
import { api } from '@/lib/api'
import { ErrorPanel } from '@/components/ErrorPanel'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { projectsQuery } from '@/features/projects/queries'

/** Creating a project: the slug agents will address it by, and a display name that can change. */
export function NewProjectForm() {
  const queryClient = useQueryClient()
  const [slug, setSlug] = useState('')
  const [name, setName] = useState('')

  const create = useMutation({
    mutationFn: () => api.createProject(slug.trim(), name.trim()),
    onSuccess: async () => {
      setSlug('')
      setName('')
      await queryClient.invalidateQueries(projectsQuery())
    },
  })

  return (
    <Card>
      <CardHeader>
        <CardTitle className="text-base">New project</CardTitle>
      </CardHeader>
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
          {create.error ? <ErrorPanel error={create.error} /> : null}
          <Button type="submit" disabled={create.isPending}>
            {create.isPending ? 'Creating…' : 'Create project'}
          </Button>
        </form>
      </CardContent>
    </Card>
  )
}
