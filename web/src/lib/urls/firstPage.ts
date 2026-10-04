import { stripSearchParams, type SearchMiddleware } from '@tanstack/react-router'

/**
 * The search middleware the root route declares, so a link to the first page of any paged view is
 * its plain URL wherever it was built: the pager, the sidebar, a filter that starts the list over.
 * History used to write `?page=1` while a commit's files left it out, because each view decided for
 * itself; on the root, a paged view added later cannot forget it.
 *
 * It shapes the links the app builds and nothing else. A pasted URL with `?page=1` still opens the
 * first page, because the route's `validateSearch` reads it as it reads any page.
 */
export function bareFirstPage<T extends { page?: number }>(): SearchMiddleware<T> {
  // The cast is the library's own type in another spelling: an object of defaults is a partial of
  // the schema, and `page` is the one key of it every paged schema shares. The compiler cannot see
  // that for a `T` it has not met: one could declare `page` as the literal 2, which `1` is not.
  // oxlint-disable-next-line typescript/no-unsafe-type-assertion
  return stripSearchParams<T>({ page: 1 } as Partial<T>)
}
