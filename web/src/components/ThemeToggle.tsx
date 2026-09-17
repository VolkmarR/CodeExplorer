import { Moon, Sun } from 'lucide-react'
import { useSyncExternalStore } from 'react'
import { Button } from '@/components/ui/button'
import { Tooltip, TooltipContent, TooltipTrigger } from '@/components/ui/tooltip'
import { chooseNextTheme, currentTheme, subscribeToTheme } from '@/lib/theme'

/**
 * The one control over which theme is on screen.
 *
 * It holds no state of its own: the inline script in `index.html` has already put the class on
 * `<html>` before React ran, so this reads the document back rather than re-deciding what the page
 * is already wearing. `useSyncExternalStore` is how that read stays correct — the document changes
 * from a click here, and from the system's own preference for a reader who has not made one.
 */
export function ThemeToggle() {
  const theme = useSyncExternalStore(subscribeToTheme, currentTheme, () => 'dark')
  const label = `Switch to the ${theme === 'dark' ? 'light' : 'dark'} theme`

  return (
    <Tooltip>
      <TooltipTrigger
        render={
          <Button variant="ghost" size="icon-sm" onClick={chooseNextTheme}>
            {theme === 'dark' ? <Sun /> : <Moon />}
            <span className="sr-only">{label}</span>
          </Button>
        }
      />
      <TooltipContent>{label}</TooltipContent>
    </Tooltip>
  )
}
