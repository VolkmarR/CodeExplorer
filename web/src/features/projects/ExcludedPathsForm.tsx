import { useMutation, useQuery, useQueryClient, useSuspenseQuery } from '@tanstack/react-query'
import { useState } from 'react'
import { type ExcludedPathSuggestion, saveExcludedPaths } from '@/features/projects/api'
import { withPattern } from '@/features/projects/excludedPaths'
import {
  excludedPathSuggestionsQuery,
  excludedPathsQuery,
  invalidateProject,
} from '@/features/projects/queries'
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

  // Proposals are never saved: Add copies one into the draft and Save stays the only write (#217).
  // The button starts the read, not the page; `settled` holds the ones added or dropped since.
  const suggest = useQuery({ ...excludedPathSuggestionsQuery(project), enabled: false })
  const [settled, setSettled] = useState<ReadonlySet<string>>(new Set())
  const suggestions = suggest.data?.suggestions.filter((s) => !settled.has(s.pattern))
  const settle = (patterns: string[], keep: boolean) => {
    if (keep) setDraft(patterns.reduce(withPattern, draft))
    setSettled(new Set([...settled, ...patterns]))
  }

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
          {suggest.data?.unavailable ? (
            <p className="text-sm text-muted-foreground">{suggest.data.unavailable}</p>
          ) : suggestions ? (
            <SuggestionList
              suggestions={suggestions}
              onSettle={(pattern, keep) => settle([pattern], keep)}
              onAddAll={() =>
                settle(
                  suggestions.map((s) => s.pattern),
                  true,
                )
              }
            />
          ) : null}
          {save.error ? <ErrorPanel error={save.error} /> : null}
          {suggest.error ? <ErrorPanel error={suggest.error} /> : null}
          <div className="flex flex-wrap gap-2">
            <Button type="submit" disabled={save.isPending}>
              {save.isPending ? 'Saving…' : 'Save'}
            </Button>
            <Button
              type="button"
              variant="outline"
              disabled={suggest.isFetching}
              onClick={() => {
                setSettled(new Set())
                void suggest.refetch()
              }}
            >
              {suggest.isFetching ? 'Suggesting…' : 'Suggest from repositories'}
            </Button>
          </div>
        </form>
      </CardContent>
    </Card>
  )
}

const RULE_LABELS: Record<ExcludedPathSuggestion['rule'], string> = {
  GitAttributes: '.gitattributes',
  WellKnownName: 'Well-known name',
  History: 'History',
}

/**
 * The proposals awaiting a decision. Each says its rule, why and how many files at HEAD it matches,
 * which is what the operator judges it by; Add puts it in the draft and Drop forgets it.
 */
function SuggestionList({
  suggestions,
  onSettle,
  onAddAll,
}: {
  suggestions: ExcludedPathSuggestion[]
  onSettle: (pattern: string, keep: boolean) => void
  onAddAll: () => void
}) {
  if (suggestions.length === 0)
    return <p className="text-sm text-muted-foreground">Nothing more to suggest.</p>

  return (
    <div className="space-y-2 rounded-md border p-3">
      <div className="flex items-center justify-between gap-2">
        <p className="text-sm font-medium">Suggestions: Add puts one in the list above, unsaved</p>
        <Button type="button" variant="ghost" size="xs" onClick={onAddAll}>
          Add all
        </Button>
      </div>
      <ul className="divide-y">
        {suggestions.map((s) => (
          <li key={s.pattern} className="flex flex-wrap items-center gap-x-3 gap-y-1 py-2">
            <div className="min-w-0 flex-1">
              <code className="text-sm break-all">{s.pattern}</code>
              <p className="text-xs text-muted-foreground">
                {RULE_LABELS[s.rule]} · {s.reason} · {s.files} {s.files === 1 ? 'file' : 'files'} at
                HEAD
              </p>
            </div>
            <div className="flex gap-1">
              <Button
                type="button"
                variant="outline"
                size="xs"
                onClick={() => onSettle(s.pattern, true)}
              >
                Add
              </Button>
              <Button
                type="button"
                variant="ghost"
                size="xs"
                onClick={() => onSettle(s.pattern, false)}
              >
                Drop
              </Button>
            </div>
          </li>
        ))}
      </ul>
    </div>
  )
}
