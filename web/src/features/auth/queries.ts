import { queryOptions } from '@tanstack/react-query'
import { fetchAuthStatus } from '@/lib/auth'
import { authKey } from '@/lib/queryKeys'

/** Whether there is a tenant, and who is signed in to it. Asked once and shared by the whole frame. */
export function authQuery() {
  return queryOptions({ queryFn: () => fetchAuthStatus(), queryKey: authKey })
}
