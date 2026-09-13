import { useParams, useSearch } from '@tanstack/react-router'
import { BrowseFilter } from '@/features/files/BrowseFilter'
import { FileList } from '@/features/files/FileList'
import { FileTree } from '@/features/files/FileTree'

/**
 * Browsing a project's index: what is in there, rather than what matches a query. The pair with
 * search, and the answer to "I don't know what to search for yet".
 */
export function BrowsePage() {
  const { project } = useParams({ from: '/projects/$project/files' })
  const search = useSearch({ from: '/projects/$project/files' })

  return (
    <div className="space-y-6">
      {/* No heading and no links of its own: the header bar says which project this is and which of
          its three views is open, and the breadcrumb below says where in the tree. */}
      <BrowseFilter project={project} search={search} />
      {/* A glob answers "where is every X", a tree answers "what is in here". The URL says which was
          asked, so either view can be linked. */}
      {search.glob === '' ? (
        <FileTree project={project} path={search.path} />
      ) : (
        <FileList project={project} search={search} />
      )}
    </div>
  )
}
