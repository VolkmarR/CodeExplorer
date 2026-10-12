import { useMutation, useQueryClient } from '@tanstack/react-query'
import { useState } from 'react'
import { addRepository } from '@/features/projects/api'
import { ErrorPanel } from '@/components/ErrorPanel'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { toast } from '@/components/ui/toast'
import { invalidateProject } from '@/features/projects/queries'
import {
  type RepositoryProvider,
  type RepositoryUrlShape,
  repositoryUrlShape,
  withoutAzureDevOpsUserName,
} from '@/features/projects/repositoryUrl'

const providerName: Record<RepositoryProvider, string> = {
  local: 'Local repository',
  'azure-devops': 'Azure DevOps',
  github: 'GitHub',
  gitlab: 'GitLab',
  other: 'Git server',
}

// The token alone in every case: the server sends it as the password, under a user name of its own.
const credentialHelp: Record<RepositoryProvider, string> = {
  local: '',
  'azure-devops':
    'A personal access token with the Code (Read) scope, for the organization in the URL. The token alone, no user name.',
  github:
    'A fine-grained personal access token with Contents: Read on this repository, or a classic token with the repo scope. None for a public repository.',
  gitlab:
    'A personal, project or group access token with the read_repository scope. None for a public project.',
  other:
    'A token your git server accepts as the password over https. None for a public repository.',
}

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
  // The user name taken out of a pasted Azure DevOps URL, said so for as long as the field still
  // holds the URL that took it out: a later paste or edit is a different URL with nothing removed.
  const [removed, setRemoved] = useState<{ url: string; userName: string } | null>(null)
  const shape = repositoryUrlShape(url)
  // A local path is read from the server's own disk, where a credential has nothing to sign in to.
  const local = shape?.provider === 'local'

  const add = useMutation({
    mutationFn: () =>
      addRepository(
        project,
        slug.trim(),
        url.trim(),
        local || credential === '' ? null : credential,
      ),
    // No reset of the fields: `onAdded` closes the form, and the table it sits in unmounts it.
    onSuccess: async (created) => {
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
      {/* A single-repository project heads no path with a slug, so there is nothing to choose:
          the server assigns one and it is never shown in a path (ADR-0006). Half a row, as a slug
          is a word; the URL below takes the full row, as the credential does. */}
      {singleRepository ? null : (
        <div className="grid gap-4 sm:grid-cols-2">
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
        </div>
      )}
      <div className="space-y-2">
        <Label htmlFor="repository-url">Git URL</Label>
        <Input
          id="repository-url"
          value={url}
          onChange={(event) => {
            const cleaned = withoutAzureDevOpsUserName(event.target.value)
            setUrl(cleaned?.url ?? event.target.value)
            setRemoved(cleaned)
          }}
          placeholder="https://github.com/acme/platform.git"
          font="mono"
          required
        />
        {shape ? (
          <RepositoryUrlHint
            shape={shape}
            removedUserName={removed?.url === url ? removed.userName : null}
            onFix={(fixed, secret) => {
              setUrl(fixed)
              if (secret !== null) setCredential(secret)
            }}
          />
        ) : null}
      </div>
      {local ? null : (
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
            {credentialHelp[shape?.provider ?? 'other']} Stored encrypted and never shown again —
            afterwards it reads only as set or not set.
          </p>
        </div>
      )}
      {add.error ? <ErrorPanel error={add.error} /> : null}
      <Button type="submit" disabled={add.isPending}>
        {add.isPending ? 'Adding…' : 'Add repository'}
      </Button>
    </form>
  )
}

/**
 * Which host the URL points at, and the one thing to change before the server would refuse it or the
 * first refresh would fail. Azure DevOps' clone dialog copies its URL with the organization as a user
 * name, which the form removes as it is pasted; any other host's user name waits for the fix button.
 */
function RepositoryUrlHint({
  shape,
  removedUserName,
  onFix,
}: {
  shape: RepositoryUrlShape
  removedUserName: string | null
  onFix: (url: string, secret: string | null) => void
}) {
  const name = providerName[shape.provider]
  const fixedUrl = shape.withoutUserPart
  let note: string | null = null
  let fix: string | null = null

  if (shape.provider === 'local') {
    note =
      "Read from the server's own disk, so there is no credential. Accepted only where the server sets Control:AllowLocalRepositories to true."
  } else if (shape.secret !== null) {
    note = 'The URL carries a password or token, which the server refuses in a URL.'
    fix = 'Move it to the credential field'
  } else if (shape.userName !== null) {
    note = `The server refuses the user name "${shape.userName}" before the host, and does not need it.`
    fix = 'Remove it'
  } else if (shape.ssh && shape.provider !== 'other') {
    note = `${name} signs in over ssh with a key, and this server signs in with a token. Use the repository's https clone URL.`
  } else if (removedUserName !== null) {
    note = `Removed the user name "${removedUserName}" the clone dialog adds; the token alone signs in.`
  }

  return (
    <div className="space-y-1 text-xs text-muted-foreground">
      <p>
        <span className="font-medium text-foreground">{name}</span>
        {note === null ? null : ` — ${note}`}
      </p>
      {fix !== null && fixedUrl !== null ? (
        <Button
          type="button"
          variant="outline"
          size="xs"
          onClick={() => onFix(fixedUrl, shape.secret)}
        >
          {fix}
        </Button>
      ) : null}
    </div>
  )
}
