import { useMutation, useQueryClient } from '@tanstack/react-query'
import { api } from '@/lib/api'
import { refreshStatusQuery } from '@/features/refresh/queries'

/**
 * Starting a refresh, from wherever the operator is.
 *
 * A hook rather than a mutation written at each call site, because two places start one — the
 * button on the settings page and the project menu in the sidebar — and the part that is easy to
 * get wrong is not the POST but what happens after it: the status the POST answers with is already
 * the first poll, so seeding it is what makes the progress line appear immediately instead of a
 * second later when the interval comes round. Written twice, that is two chances to forget.
 *
 * Nothing here reports the failure. A refusal — another refresh running, too little disk — is the
 * server's own prose, and where it belongs differs by caller: a panel on the settings page, a toast
 * from a menu item that has no panel to fill.
 */
export function useRefreshProject(slug: string) {
  const queryClient = useQueryClient()

  return useMutation({
    mutationFn: () => api.refresh(slug),
    onSuccess: (started) => queryClient.setQueryData(refreshStatusQuery(slug).queryKey, started),
  })
}
