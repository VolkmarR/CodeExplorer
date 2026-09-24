import { describe, expect, it } from 'vite-plus/test'
import type { OverviewFolderCoupling, RepositoryCoupling } from '@/features/projects/api'
import { featuredRepository, pairShare, sharedCommits } from '@/features/projects/folderCoupling'

const one: RepositoryCoupling = {
  repositorySlug: 'one',
  commits: 10,
  folders: [
    { folder: 'src', commits: 8 },
    { folder: 'lib', commits: 4 },
  ],
  pairs: [{ first: 'lib', second: 'src', commits: 3 }],
}
const two: RepositoryCoupling = { repositorySlug: 'two', commits: 2, folders: [], pairs: [] }
const coupling: OverviewFolderCoupling = {
  repositories: [one, two],
  maxCommitPaths: 200,
  ceilingExcluded: 0,
}

describe('featuredRepository', () => {
  it('takes the selected repository', () => {
    expect(featuredRepository(coupling, 'two')).toBe(two)
  })

  it('takes the busiest one when all are selected', () => {
    expect(featuredRepository(coupling, undefined)).toBe(one)
  })
})

describe('pairShare', () => {
  it('divides by the quieter folder', () => {
    expect(pairShare(one, one.pairs[0])).toBe(0.75)
  })
})

describe('sharedCommits', () => {
  it('finds a pair in either order', () => {
    expect(sharedCommits(one, 'src', 'lib')).toBe(3)
    expect(sharedCommits(one, 'lib', 'src')).toBe(3)
    expect(sharedCommits(one, 'lib', 'docs')).toBe(0)
  })
})
