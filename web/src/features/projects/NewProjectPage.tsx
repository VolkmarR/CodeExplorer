import { useNavigate } from '@tanstack/react-router'
import { NewProjectForm } from '@/features/projects/NewProjectForm'

/**
 * Creating a project on a page of its own. Both leaving it and finishing on it return to the list,
 * which is where the new project appears.
 */
export function NewProjectPage() {
  const navigate = useNavigate()
  const toList = () => navigate({ to: '/' })

  return (
    <div className="max-w-2xl space-y-6">
      <div>
        <h1 className="text-2xl font-semibold tracking-tight">New project</h1>
        <p className="mt-1 text-sm text-muted-foreground">
          A project is what an agent connects to, and what a search spans. Add its repositories once
          it exists.
        </p>
      </div>

      <NewProjectForm onCancel={toList} onCreated={toList} />
    </div>
  )
}
