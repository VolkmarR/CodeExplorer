import { useMutation, useQueryClient } from '@tanstack/react-query'
import { Plus } from 'lucide-react'
import { useState } from 'react'
import type { ProjectDetail } from '@/lib/api'
import { api } from '@/lib/api'
import { formatCount, formatTime, shortSha } from '@/lib/format'
import { CommitLine } from '@/components/CommitLine'
import { ConfirmDialog } from '@/components/ConfirmDialog'
import { ErrorPanel } from '@/components/ErrorPanel'
import { NewRepositoryForm } from '@/features/projects/NewRepositoryForm'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'
import {
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
} from '@/components/ui/table'
import { toast } from '@/components/ui/toast'
import { invalidateProject } from '@/features/projects/queries'

/** What a row says in place of a value the last build has not produced yet. */
const NOT_INDEXED = 'not indexed yet'

/**
 * A project's repositories as configured, with where the last build found each. A repository added
 * since that build has no commit, no counts and no time, which is how the page shows a rebuild is
 * owed. The build time is the project's: a refresh rebuilds every repository together (CONTEXT.md),
 * so there is no per-repository time to report and the column repeats the one there is.
 *
 * Adding one is a form behind a button in the card's header rather than a second card below: it is
 * done once per repository and then never, and a form that is always open reads as something left
 * unfinished. It opens by itself while there is nothing in the table, since then it is the page.
 */
export function RepositoryTable({ project }: { project: ProjectDetail }) {
  const queryClient = useQueryClient()
  const { slug, repositories, singleRepository } = project
  const [adding, setAdding] = useState(false)

  const remove = useMutation({
    mutationFn: (repository: string) => api.removeRepository(slug, repository),
    onSuccess: async (_, repository) => {
      await invalidateProject(queryClient, slug)
      toast.add({
        description: 'Refresh to rebuild the index without it.',
        title: `Removed ${repository}`,
        type: 'success',
      })
    },
  })

  // A single-repository project takes its one and no more, and the declaration cannot be undone,
  // so once it is full there is no form to offer — only the reason there is none (ADR-0006).
  const full = singleRepository && repositories.length > 0
  const empty = repositories.length === 0

  return (
    <Card>
      <CardHeader className="flex flex-row items-center justify-between gap-4">
        <CardTitle className="text-base">Repositories</CardTitle>
        {full ? null : (
          <Button
            variant="outline"
            size="sm"
            aria-expanded={adding || empty}
            disabled={empty}
            onClick={() => setAdding(!adding)}
          >
            <Plus />
            Add repository
          </Button>
        )}
      </CardHeader>
      <CardContent className="gap-4">
        {empty ? (
          <p className="text-sm text-muted-foreground">No repositories yet. Add the first below.</p>
        ) : (
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead>Slug</TableHead>
                <TableHead>Git URL</TableHead>
                <TableHead>Credential</TableHead>
                <TableHead>Commit</TableHead>
                <TableHead>Indexed</TableHead>
                <TableHead className="text-right">Contents</TableHead>
                <TableHead />
              </TableRow>
            </TableHeader>
            <TableBody>
              {repositories.map((repository) => (
                <TableRow key={repository.slug}>
                  <TableCell className="font-mono">{repository.slug}</TableCell>
                  <TableCell
                    className="max-w-xs font-mono text-xs text-muted-foreground"
                    title={repository.url}
                  >
                    {shortUrl(repository.url)}
                  </TableCell>
                  <TableCell>
                    <Badge variant={repository.hasCredential ? 'secondary' : 'outline'}>
                      {repository.hasCredential ? 'set' : 'not set'}
                    </Badge>
                  </TableCell>
                  <TableCell className="text-xs">
                    {/* The commit the index was built from, and under it the history the index holds
                        for the repository: how many commits, and who made the newest and when. One
                        column, because the newest commit is this commit. A repository that was indexed
                        but has no commits is one whose history never arrived, and it must not read
                        like one nobody has changed. */}
                    {repository.headCommit === null ? (
                      <span className="font-mono">{NOT_INDEXED}</span>
                    ) : (
                      <span className="flex flex-col gap-0.5">
                        <span className="font-mono">{shortSha(repository.headCommit)}</span>
                        {repository.newestCommit === null ? (
                          <span className="text-muted-foreground">no history</span>
                        ) : (
                          <span className="flex flex-wrap items-baseline gap-x-2 whitespace-nowrap">
                            <span className="text-muted-foreground tabular-nums">
                              {formatCount(repository.commits)} commits
                            </span>
                            <CommitLine
                              commit={repository.newestCommit}
                              subject={false}
                              sha={false}
                            />
                          </span>
                        )}
                      </span>
                    )}
                  </TableCell>
                  <TableCell className="text-sm text-muted-foreground">
                    {repository.headCommit ? formatTime(project.index.builtAt) : NOT_INDEXED}
                  </TableCell>
                  <TableCell className="text-right text-sm text-muted-foreground tabular-nums">
                    {repository.fileCount === null
                      ? NOT_INDEXED
                      : `${formatCount(repository.fileCount)} files, ${formatCount(repository.lineCount ?? 0)} lines`}
                  </TableCell>
                  <TableCell className="text-right">
                    <ConfirmDialog
                      trigger="Remove"
                      title={`Remove ${repository.slug}?`}
                      description="The repository leaves the project and its local copy is deleted. Its files stay searchable until the next refresh rebuilds the index without them."
                      action="Remove repository"
                      disabled={remove.isPending}
                      onConfirm={() => remove.mutate(repository.slug)}
                    />
                  </TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        )}
        {remove.error ? <ErrorPanel error={remove.error} /> : null}
        {full ? (
          <p className="text-sm text-muted-foreground">
            A single-repository project holds the one repository above and cannot take another.
            Create a separate project for a second repository.
          </p>
        ) : null}
        {adding || empty ? (
          <NewRepositoryForm
            project={slug}
            singleRepository={singleRepository}
            onAdded={() => setAdding(false)}
          />
        ) : null}
      </CardContent>
    </Card>
  )
}

/**
 * A git URL down to what tells two apart at a glance: the host and the last path segment. The scheme
 * and the organisation in between are the same for every repository of one operator, and the full
 * URL is one hover away in the title.
 */
function shortUrl(url: string): string {
  try {
    const parsed = new URL(url)
    // A local path (`C:\repos\x`) parses too, as a scheme with no host, and would shorten to `/…/x`.
    if (parsed.host === '') return url
    const last = parsed.pathname.split('/').findLast(Boolean) ?? ''
    return `${parsed.host}/…/${last}`
  } catch {
    // Not a URL the browser parses — an scp-style `git@host:org/repo.git` — so it shows as written.
    return url
  }
}
