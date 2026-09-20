import { useSuspenseQuery } from '@tanstack/react-query'
import { useNavigate } from '@tanstack/react-router'
import { CaseSensitive, Regex, Search as SearchIcon } from 'lucide-react'
import { useDraft } from '@/hooks/useDraft'
import { projectQuery } from '@/features/projects/queries'
import { searchQuery } from '@/features/search/queries'
import type { SearchParameters } from '@/lib/urls/searchParams'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Separator } from '@/components/ui/separator'
import { ToggleGroup, ToggleGroupItem } from '@/components/ui/toggle-group'
import { Tooltip, TooltipContent, TooltipTrigger } from '@/components/ui/tooltip'
import { RepositorySelect } from '@/features/projects/RepositorySelect'
import { formatCount } from '@/lib/format'

/** Every repository: the select's value for "no scope", since Base UI wants a value and not undefined. */
const ALL = ''

/**
 * The two switches that change what the text in the box means, and the field of the search each
 * one is. A table rather than two hand-written toggles, so the group's value and the draft it edits
 * cannot come to disagree about which switch is which.
 */
const MODES = [
  {
    Icon: Regex,
    description: 'Read the query as a regular expression. RE2: no lookaround, no backreferences.',
    field: 'regex',
    label: 'regex',
  },
  {
    Icon: CaseSensitive,
    description: 'Match the case of the query exactly.',
    field: 'caseSensitive',
    label: 'case',
  },
] as const satisfies readonly {
  Icon: typeof Regex
  description: string
  field: 'regex' | 'caseSensitive'
  label: string
}[]

/**
 * Edits the search in the URL. The inputs are local only until submit, because navigating on every
 * keystroke would put a history entry behind each letter and run a query for each prefix.
 *
 * The search endpoint has no repository parameter — a repository is the first segment of a path, so
 * scoping to one is the glob `slug/*`. The form offers it as a select all the same, because picking a
 * repository is the common narrowing and typing its slug with a star after it is not how anyone would
 * guess to do that. A glob the operator typed wins over the select: the two are the same field.
 */
export function SearchForm({ project, search }: { project: string; search: SearchParameters }) {
  const navigate = useNavigate()
  const { data: detail } = useSuspenseQuery(projectQuery(project))
  const [draft, setDraft] = useDraft(search)

  // Which switches are on, as the names the group speaks in.
  const pressed = MODES.filter((mode) => draft[mode.field]).map((mode) => mode.field)

  // Which repository the path glob names, if it is exactly the shape the select writes.
  const scoped = detail.repositories.find((r) => `${r.slug}/*` === draft.path)?.slug ?? ALL
  const multiRepository = !detail.singleRepository && detail.repositories.length > 1

  return (
    <form
      className="space-y-3"
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
      <div className="flex gap-2">
        <div className="relative min-w-0 flex-1">
          <SearchIcon
            aria-hidden="true"
            className="absolute top-1/2 left-2.5 size-4 -translate-y-1/2 text-muted-foreground"
          />
          <Label htmlFor="search-query" className="sr-only">
            Query
          </Label>
          <Input
            id="search-query"
            value={draft.q}
            onChange={(event) => setDraft({ ...draft, q: event.target.value })}
            placeholder="Search code in this project"
            className="pl-8 font-mono"
            // The page is for typing a query; it should not take a click to start. The rule guards
            // against focus stolen from content a reader was in, and this page has none before the box.
            // oxlint-disable-next-line jsx-a11y/no-autofocus, react-doctor/no-autofocus
            autoFocus
          />
        </div>
        <Button type="submit">Search</Button>
      </div>

      {/* One row: how the query is read, what it is narrowed to, and what came back. The two
          switches are pressed toggles and not checkboxes because which one is on changes what the
          text in the box means — they belong to the query, not to a list of options about it.
          A group rather than two loose toggles, and one that takes both at once: they are the two
          halves of one answer to "how should this text be read", and neither excludes the other. */}
      <div className="flex flex-wrap items-center gap-2">
        <ToggleGroup
          size="sm"
          variant="outline"
          spacing={0}
          // Base UI's group is single-select by default, which here would mean turning on the regex
          // switch silently turned off the case one. They are not alternatives.
          multiple
          value={pressed}
          onValueChange={(modes: string[]) =>
            setDraft({
              ...draft,
              caseSensitive: modes.includes('caseSensitive'),
              regex: modes.includes('regex'),
            })
          }
        >
          {MODES.map((mode) => (
            <Tooltip key={mode.field}>
              <TooltipTrigger
                render={
                  <ToggleGroupItem value={mode.field} aria-label={mode.description}>
                    <mode.Icon />
                    <span className="text-xs">{mode.label}</span>
                  </ToggleGroupItem>
                }
              />
              <TooltipContent>{mode.description}</TooltipContent>
            </Tooltip>
          ))}
        </ToggleGroup>

        <Separator orientation="vertical" className="mx-1 h-6" />

        {multiRepository ? (
          <div className="flex items-center gap-2">
            <Label htmlFor="search-repository" className="text-xs text-muted-foreground">
              Repository
            </Label>
            <div className="w-44">
              <RepositorySelect
                id="search-repository"
                repositories={detail.repositories}
                value={scoped}
                onChange={(slug) =>
                  setDraft({ ...draft, path: slug === ALL ? undefined : `${slug}/*` })
                }
              />
            </div>
          </div>
        ) : null}
        <div className="flex items-center gap-2">
          {/* "Extension" and not "Language": the index filters on the extension, and a project may
              write one language in two of them. Calling it the language would promise a mapping the
              server does not make. */}
          <Label htmlFor="search-extension" className="text-xs text-muted-foreground">
            Extension
          </Label>
          <Input
            id="search-extension"
            value={draft.extension ?? ''}
            onChange={(event) => setDraft({ ...draft, extension: event.target.value || undefined })}
            placeholder="prg"
            className="h-8 w-24 font-mono"
          />
        </div>
        <div className="flex items-center gap-2">
          <Label htmlFor="search-path" className="text-xs text-muted-foreground">
            Path
          </Label>
          <Input
            id="search-path"
            value={draft.path ?? ''}
            onChange={(event) => setDraft({ ...draft, path: event.target.value || undefined })}
            placeholder="*/src/*"
            className="h-8 w-44 font-mono"
          />
        </div>

        {/* On the right of the same row, because it is the answer to everything to its left. Only
            once there is a query: before that there is nothing to count, and the loader has not
            run. */}
        {search.q === '' ? null : <SearchStats project={project} search={search} />}
      </div>
    </form>
  )
}

/**
 * How much came back and how long it took. It reads the same query the results below do, so it is
 * the cache entry the loader primed rather than a second request.
 */
function SearchStats({ project, search }: { project: string; search: SearchParameters }) {
  const { data } = useSuspenseQuery(searchQuery(project, search))
  const { result, elapsedMs } = data

  return (
    <p className="ml-auto flex items-center gap-2 text-xs text-muted-foreground tabular-nums">
      {/* Which engine answered, beside the timing it explains: a substring scan is slower than
          full-text search and ranks differently, and the number next to it is why that matters. */}
      <Badge variant="secondary">{result.engine}</Badge>
      <span>
        <span className="font-medium text-foreground">{formatCount(result.totalLines)}</span>{' '}
        {result.totalLines === 1 ? 'match' : 'matches'} in{' '}
        <span className="font-medium text-foreground">{formatCount(result.totalFiles)}</span>{' '}
        {result.totalFiles === 1 ? 'file' : 'files'} · {formatCount(elapsedMs)} ms
      </span>
    </p>
  )
}
