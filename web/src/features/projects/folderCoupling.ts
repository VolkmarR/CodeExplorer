import type {
  FolderPair,
  OverviewFolderCoupling,
  RepositoryCoupling,
} from '@/features/projects/api'

/**
 * The repository the heatmap draws: the one the filter bar selected, or the one with the most commits
 * in the window when all are selected. The server sends them most commits first.
 */
export function featuredRepository(
  coupling: OverviewFolderCoupling,
  repository: string | undefined,
): RepositoryCoupling | undefined {
  return (
    coupling.repositories.find((r) => r.repositorySlug === repository) ?? coupling.repositories[0]
  )
}

/** A pair's shared commits as a share of the quieter folder's commits, 0 to 1. */
export function pairShare(repository: RepositoryCoupling, pair: FolderPair): number {
  const commits = (folder: string) =>
    repository.folders.find((f) => f.folder === folder)?.commits ?? 0
  const smaller = Math.min(commits(pair.first), commits(pair.second))
  return smaller === 0 ? 0 : pair.commits / smaller
}

/** The shared commits of two folders, in either order, or 0 where they share none. */
export function sharedCommits(repository: RepositoryCoupling, a: string, b: string): number {
  const [first, second] = a < b ? [a, b] : [b, a]
  return repository.pairs.find((p) => p.first === first && p.second === second)?.commits ?? 0
}
