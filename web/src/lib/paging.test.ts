import { expect, test } from 'vite-plus/test'
import { pageSpan } from '@/lib/paging'

test('a page spans its rows, and the last page is short', () => {
  expect(pageSpan(1, 50, 120)).toEqual({ first: 1, last: 50, lastPage: 3, page: 1 })
  expect(pageSpan(2, 50, 120)).toEqual({ first: 51, last: 100, lastPage: 3, page: 2 })
  expect(pageSpan(3, 50, 120)).toEqual({ first: 101, last: 120, lastPage: 3, page: 3 })
})

test('an exact multiple of the page size has no empty page after it', () => {
  expect(pageSpan(2, 50, 100).lastPage).toBe(2)
})

test('an empty list is one page with no rows', () => {
  expect(pageSpan(1, 50, 0)).toEqual({ first: 1, last: 0, lastPage: 1, page: 1 })
})

test('a page past either end is answered as the nearest page there is', () => {
  expect(pageSpan(9, 50, 120)).toEqual({ first: 101, last: 120, lastPage: 3, page: 3 })
  expect(pageSpan(0, 50, 120).page).toBe(1)
})
