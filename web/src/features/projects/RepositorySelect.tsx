import type { RepositoryDetail } from '@/features/projects/api'
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from '@/components/ui/select'

/**
 * One of a project's repositories, or all of them. The search form and the browse filter both narrow
 * to a repository, and both used to ask for its slug in a text box — which the operator had to know
 * or go and look up. The project already knows them, so this offers them.
 *
 * The empty string is "all", because Base UI's select wants a value for every item and `null` is
 * what it reports for nothing chosen, which reads as a cleared field rather than as a choice.
 */
export function RepositorySelect({
  id,
  repositories,
  value,
  onChange,
}: {
  id: string
  repositories: RepositoryDetail[]
  value: string
  onChange: (slug: string) => void
}) {
  return (
    <Select value={value} onValueChange={(next) => onChange(next ?? '')}>
      <SelectTrigger id={id} className="w-full">
        <SelectValue>
          {value === '' ? 'All' : <span className="font-mono">{value}</span>}
        </SelectValue>
      </SelectTrigger>
      <SelectContent>
        <SelectItem value="">All repositories</SelectItem>
        {repositories.map((repository) => (
          <SelectItem key={repository.slug} value={repository.slug}>
            <span className="font-mono">{repository.slug}</span>
          </SelectItem>
        ))}
      </SelectContent>
    </Select>
  )
}
