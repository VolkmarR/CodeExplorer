import { useMutation, useQueryClient } from '@tanstack/react-query'
import { useState } from 'react'
import { addRepository } from '@/features/projects/api'
import { ErrorPanel } from '@/components/ErrorPanel'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { toast } from '@/components/ui/toast'
import { invalidateProject } from '@/features/projects/queries'

/**
 * Adding a repository. The credential is write-only everywhere: it is typed once here and the API
 * never hands it back, so there is nothing to prefill and no field to edit — a new one replaces it.
 * Rendered inside the repositories card, under the table it adds to.
 */
export function NewRepositoryForm({
  project,
  singleRepository,
  onAdded,
}: {
  project: string
  singleRepository: boolean
  onAdded: () => void
}) {
  const queryClient = useQueryClient()
  const [slug, setSlug] = useState('')
  const [url, setUrl] = useState('')
  const [credential, setCredential] = useState('')

  const add = useMutation({
    mutationFn: () =>
      addRepository(project, slug.trim(), url.trim(), credential === '' ? null : credential),
    onSuccess: async (created) => {
      setSlug('')
      setUrl('')
      setCredential('')
      await invalidateProject(queryClient, project)
      toast.add({
        description: 'Refresh to clone and index it.',
        title: `Added ${created.slug}`,
        type: 'success',
      })
      onAdded()
    },
  })

  return (
    <form
      onSubmit={(event) => {
        event.preventDefault()
        add.mutate()
      }}
      className="space-y-4 rounded-lg border bg-muted/30 p-4"
    >
      <div className="grid gap-4 sm:grid-cols-2">
        {/* A single-repository project heads no path with a slug, so there is nothing to choose:
            the server assigns one and it is never shown in a path (ADR-0006). */}
        {singleRepository ? null : (
          <div className="space-y-2">
            <Label htmlFor="repository-slug">Slug</Label>
            <Input
              id="repository-slug"
              value={slug}
              onChange={(event) => setSlug(event.target.value)}
              placeholder="platform"
              font="mono"
              required
            />
            <p className="text-xs text-muted-foreground">
              The first segment of every qualified path in this repository. Yours to choose, so
              moving the remote does not rename the paths agents quote.
            </p>
          </div>
        )}
        <div className="space-y-2">
          <Label htmlFor="repository-url">Git URL</Label>
          <Input
            id="repository-url"
            value={url}
            onChange={(event) => setUrl(event.target.value)}
            placeholder="https://github.com/acme/platform.git"
            font="mono"
            required
          />
        </div>
      </div>
      <div className="space-y-2">
        <Label htmlFor="repository-credential">Credential (optional)</Label>
        <Input
          id="repository-credential"
          type="password"
          value={credential}
          onChange={(event) => setCredential(event.target.value)}
          placeholder="personal access token"
          autoComplete="off"
        />
        <p className="text-xs text-muted-foreground">
          Stored encrypted and never shown again — afterwards it reads only as set or not set.
        </p>
      </div>
      {add.error ? <ErrorPanel error={add.error} /> : null}
      <Button type="submit" disabled={add.isPending}>
        {add.isPending ? 'Adding…' : 'Add repository'}
      </Button>
    </form>
  )
}
