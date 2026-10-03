import { vi } from 'vite-plus/test'

/**
 * For tests: the URL a call through `http` asks for, read off the request ky hands to `fetch`, which
 * answers with an empty object so the call resolves. The query string is compared byte for byte,
 * because a shared link and a request that moved a parameter or dropped one read the same to a
 * reader and differently to the server.
 *
 * Node has no page to resolve `/api` against, so `Request` is given one; what the test asserts is
 * the path and the query, never the origin.
 */
export async function recordRequest(call: () => Promise<unknown>): Promise<string> {
  const requested: string[] = []
  vi.stubGlobal(
    'Request',
    class extends Request {
      constructor(input: RequestInfo | URL, init?: RequestInit) {
        super(typeof input === 'string' ? new URL(input, 'http://localhost') : input, init)
      }
    },
  )
  vi.stubGlobal('fetch', async (request: Request) => {
    const url = new URL(request.url)
    requested.push(url.pathname + url.search)
    return Response.json({})
  })
  try {
    await call()
  } finally {
    vi.unstubAllGlobals()
  }
  if (requested.length !== 1) throw new Error(`Expected one request, saw ${requested.length}.`)
  return requested[0]
}
