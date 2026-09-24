import { useMutation, useQueryClient, useSuspenseQuery } from '@tanstack/react-query'
import { saveExcludedPaths } from '@/features/projects/api'
import { excludedPathsQuery, invalidateProject } from '@/features/projects/queries'
import { ErrorPanel } from '@/components/ErrorPanel'
import { useDraft } from '@/hooks/useDraft'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'
import { Label } from '@/components/ui/label'
import { Textarea } from '@/components/ui/textarea'
import { toast } from '@/components/ui/toast'

/**
 * The paths the overview page leaves out (#216): one glob per line, matched against qualified paths.
 * It is the page's setting and nothing else's — an agent's `project_overview`, and every search, keep
 * seeing the whole index — so saving it rebuilds nothing and applies on the page's next load.
 *
 * A textarea rather than a row per pattern: the list is written once and pasted between projects far
 * more often than it is edited one entry at a time.
 */
export function ExcludedPathsForm({ project }: { project: string }) {
  const queryClient = useQueryClient()
  const { data } = useSuspenseQuery(excludedPathsQuery(project))
  // Seeded from the stored list, and re-seeded when a save comes back normalised, so the box shows
  // what was kept rather than what was typed.
  const [draft, setDraft] = useDraft(data.patterns.join('\n'))

  const save = useMutation({
    mutationFn: () => saveExcludedPaths(project, draft.split('\n')),
    onSuccess: async (saved) => {
      // The overview sits under the project's key, so this is also what makes its next load live.
      await invalidateProject(queryClient, project)
      toast.add({
        title:
          saved.patterns.length === 0
            ? 'Nothing is excluded now'
            : `Excluding ${saved.patterns.length} ${saved.patterns.length === 1 ? 'pattern' : 'patterns'}`,
        type: 'success',
      })
    },
  })

  return (
    <Card>
      <CardHeader>
        <CardTitle>Excluded paths</CardTitle>
      </CardHeader>
      <CardContent>
        <form
          onSubmit={(event) => {
            event.preventDefault()
            save.mutate()
          }}
          className="space-y-3"
        >
          <Label htmlFor="excluded-paths" className="sr-only">
            Excluded paths
          </Label>
          <Textarea
            id="excluded-paths"
            value={draft}
            onChange={(event) => setDraft(event.target.value)}
            rows={6}
            spellCheck={false}
            placeholder={'**/*.verified.txt\n**/AssemblyInfo.*\n**/*.rc'}
          />
          <p className="text-xs text-muted-foreground">
            One glob per line, matched against qualified paths and ignoring case. A pattern is
            anchored at the root unless it starts with <code>**/</code> or <code>*</code>, and{' '}
            <code>*</code> also crosses <code>/</code>; a <code>{'/**/'}</code> may also match no
            folder at all. The overview leaves these out of every section and says how many files it
            left out; agents and searches still see them.
          </p>
          {save.error ? <ErrorPanel error={save.error} /> : null}
          <Button type="submit" disabled={save.isPending}>
            {save.isPending ? 'Saving…' : 'Save'}
          </Button>
        </form>
      </CardContent>
    </Card>
  )
}
