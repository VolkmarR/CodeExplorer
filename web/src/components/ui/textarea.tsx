import * as React from 'react'
import { cn } from '@/lib/utils'

// shadcn's textarea, styled as `Input` is so a multi-line field reads as the same kind of control.
// Always in the code face: the one caller takes a pattern per line, and a field that did not would
// be the reason to add a `font` variant the way `Input` has one.
function Textarea({ className, ...props }: React.ComponentProps<'textarea'>) {
  return (
    <textarea
      data-slot="textarea"
      className={cn(
        'flex field-sizing-content min-h-16 w-full rounded-md border border-input bg-transparent px-2.5 py-2 font-mono text-base shadow-xs transition-[color,box-shadow] outline-none placeholder:text-muted-foreground focus-visible:border-ring focus-visible:ring-3 focus-visible:ring-ring/50 disabled:cursor-not-allowed disabled:opacity-50 aria-invalid:border-destructive aria-invalid:ring-3 aria-invalid:ring-destructive/20 md:text-sm dark:bg-input/30',
        className,
      )}
      {...props}
    />
  )
}

export { Textarea }
