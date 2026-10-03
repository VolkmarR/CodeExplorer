import { Link } from '@tanstack/react-router'
import { PageHeader } from '@/components/PageHeader'
import { Button } from '@/components/ui/button'

/**
 * A page that is not there. Distinct from the error panel on purpose: nothing went wrong, the URL
 * just names something that does not exist, and the way out is a link rather than a retry.
 */
export function NotFound() {
  return (
    <div className="space-y-4">
      <PageHeader
        title="Not found"
        hint="That page does not exist. A file view needs a qualified path — the repository slug, then the path inside it."
      />
      <Button render={<Link to="/" />} variant="secondary">
        Back to projects
      </Button>
    </div>
  )
}
