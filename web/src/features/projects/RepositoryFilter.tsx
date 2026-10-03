import type { RepositoryDetail } from '@/features/projects/api'
import { RepositorySelect } from '@/features/projects/RepositorySelect'
import { Label } from '@/components/ui/label'
import { cn } from '@/lib/utils'

/**
 * The labelled repository select a filter bar offers, and nothing at all where there is no choice to
 * make: one repository needs no filter, and a control that can change nothing reads as one that is
 * broken. A single-repository project has one repository at most (ADR-0006), so the count is the
 * whole test.
 *
 * The empty string is every repository, as `RepositorySelect` speaks; the URL builders translate it.
 */
export function RepositoryFilter({
  id,
  repositories,
  value,
  onChange,
  className = 'w-48',
}: {
  id: string
  repositories: RepositoryDetail[]
  value: string
  onChange: (slug: string) => void
  /** The select's width, where a bar is short of room. */
  className?: string
}) {
  if (repositories.length <= 1) return null
  return (
    <div className="flex items-center gap-2">
      <Label htmlFor={id} className="text-xs text-muted-foreground">
        Repository
      </Label>
      <div className={cn(className)}>
        <RepositorySelect id={id} repositories={repositories} value={value} onChange={onChange} />
      </div>
    </div>
  )
}
