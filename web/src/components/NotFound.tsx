import { Link } from '@tanstack/react-router'
import { Button } from '@/components/ui/button'

/**
 * A page that is not there. Distinct from the error panel on purpose: nothing went wrong, the URL
 * just names something that does not exist, and the way out is a link rather than a retry.
 */
export function NotFound() {
  return (
    <div className="space-y-4">
      <div>
        <h1 className="text-2xl font-semibold tracking-tight">Not found</h1>
        <p className="mt-1 text-sm text-muted-foreground">
          That page does not exist. A file view needs a qualified path — the repository slug, then
          the path inside it.
        </p>
      </div>
      <Button render={<Link to="/" />} variant="secondary">
        Back to projects
      </Button>
    </div>
  )
}
