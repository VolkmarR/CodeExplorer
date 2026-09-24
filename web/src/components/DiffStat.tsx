import { formatCount } from '@/lib/format'

/**
 * Lines added and removed, in the one pair of colours that means "added" and "removed" here. It is a
 * component rather than two utility classes repeated because the pair is a theme decision: a change
 * to it that reached the change log and not the churn ranking would leave one of them saying
 * something the other does not.
 */
export function DiffStat({ added, deleted }: { added: number; deleted: number }) {
  return (
    <>
      <span className="text-success">+{formatCount(added)}</span>{' '}
      <span className="text-destructive">&minus;{formatCount(deleted)}</span>
    </>
  )
}
