import { Check, Copy, Plug } from 'lucide-react'
import { useState } from 'react'
import { mcpEndpoint } from '@/features/projects/endpoints'
import { Button } from '@/components/ui/button'
import { Tooltip, TooltipContent, TooltipTrigger } from '@/components/ui/tooltip'

/**
 * The address an agent connects this project by, on every page of it.
 *
 * It is in the top bar and not on the settings page alone because it is the one string an operator
 * opens this app to fetch, and needing to navigate for it is the whole friction.
 *
 * One button rather than a chip with a button inside it: the whole thing does one thing, and a copy
 * target the size of the address is easier to hit than an icon beside it. The copy is acknowledged
 * on the button itself rather than with a toast — the bar is already where the reader is looking,
 * and a toast for a copy in the corner is a notification about something they can see.
 */
export function McpEndpoint({ project }: { project: string }) {
  const endpoint = mcpEndpoint(globalThis.location.origin, project)
  const [copied, setCopied] = useState(false)

  return (
    <Tooltip>
      <TooltipTrigger
        render={
          <Button
            variant="outline"
            size="sm"
            className="hidden sm:inline-flex"
            onClick={() => {
              void navigator.clipboard.writeText(endpoint).then(() => setCopied(true))
            }}
          >
            <Plug className="text-success" />
            {/* The path and not the whole URL: the origin is in the address bar above it, and what
                is worth reading at a glance is which project this endpoint is for. The whole thing
                is what gets copied. */}
            <span className="font-mono text-xs font-normal text-muted-foreground">
              /projects/{project}/mcp
            </span>
            {copied ? (
              <Check className="text-success" />
            ) : (
              <Copy className="text-muted-foreground" />
            )}
          </Button>
        }
      />
      <TooltipContent>{copied ? 'Copied' : `Copy ${endpoint}`}</TooltipContent>
    </Tooltip>
  )
}
