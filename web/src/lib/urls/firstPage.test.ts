import { expect, test } from 'vite-plus/test'
import { bareFirstPage } from '@/lib/urls/firstPage'

/** The route's own step, which hands on what the link asked for unchanged. */
const run = (search: { page?: number; q?: string }) =>
  bareFirstPage<{ page?: number; q?: string }>()({ next: (s) => s, search })

test('a link to the first page leaves the page out of the URL and keeps everything else', () => {
  expect(run({ page: 1, q: 'Customer' })).toEqual({ q: 'Customer' })
})

test('a link to any later page keeps it', () => {
  expect(run({ page: 2, q: 'Customer' })).toEqual({ page: 2, q: 'Customer' })
})
