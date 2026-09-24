import { expect, test } from 'vite-plus/test'
import { COMMIT_FILES_PAGE_SIZE, commitFilesPage } from './commitFilesPage'

const files = (count: number) => Array.from({ length: count }, (_, index) => index + 1)

test('a commit that fits on one page is one page of all its files', () => {
  expect(commitFilesPage(files(3), 1)).toEqual({
    page: 1,
    lastPage: 1,
    first: 1,
    rows: [1, 2, 3],
    total: 3,
  })
})

test('a commit with no files is still one page, so the pager stays hidden', () => {
  expect(commitFilesPage([], 1)).toEqual({ page: 1, lastPage: 1, first: 1, rows: [], total: 0 })
})

test('a later page starts where the one before it ended', () => {
  const page = commitFilesPage(files(COMMIT_FILES_PAGE_SIZE * 2 + 5), 2)
  expect(page.page).toBe(2)
  expect(page.lastPage).toBe(3)
  expect(page.first).toBe(COMMIT_FILES_PAGE_SIZE + 1)
  expect(page.rows).toHaveLength(COMMIT_FILES_PAGE_SIZE)
  expect(page.rows[0]).toBe(COMMIT_FILES_PAGE_SIZE + 1)
})

test('the last page holds only what is left', () => {
  expect(commitFilesPage(files(COMMIT_FILES_PAGE_SIZE * 2 + 5), 3).rows).toEqual([
    COMMIT_FILES_PAGE_SIZE * 2 + 1,
    COMMIT_FILES_PAGE_SIZE * 2 + 2,
    COMMIT_FILES_PAGE_SIZE * 2 + 3,
    COMMIT_FILES_PAGE_SIZE * 2 + 4,
    COMMIT_FILES_PAGE_SIZE * 2 + 5,
  ])
})

test('a page past the end is answered as the last page, like the server answers the file listing', () => {
  const page = commitFilesPage(files(COMMIT_FILES_PAGE_SIZE + 1), 40)
  expect(page.page).toBe(2)
  expect(page.rows).toEqual([COMMIT_FILES_PAGE_SIZE + 1])
})
