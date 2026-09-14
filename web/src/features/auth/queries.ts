import { queryOptions } from '@tanstack/react-query'
import { api } from '@/lib/api'
import { authKey } from '@/lib/queryKeys'

/** Whether there is a tenant, and who is signed in to it. Asked once and shared by the whole frame. */
export function authQuery() {
  return queryOptions({ queryFn: () => api.auth(), queryKey: authKey })
}
