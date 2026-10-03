import { useNavigate } from '@tanstack/react-router'
import { PageHeader } from '@/components/PageHeader'
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
      <PageHeader
        title="New project"
        hint="A project is what an agent connects to, and what a search spans. Add its repositories once it exists."
      />

      <NewProjectForm onCancel={toList} onCreated={toList} />
    </div>
  )
}
