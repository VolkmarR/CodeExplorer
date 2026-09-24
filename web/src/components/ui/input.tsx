import * as React from 'react'
import { Input as InputPrimitive } from '@base-ui/react/input'
import { cva, type VariantProps } from 'class-variance-authority'
import { cn } from '@/lib/utils'

// `font` and `inset` are this project's, not shadcn's: fields that take code (a path, a glob, a
// query) set it in the code face, and a field with an icon drawn inside it leaves room for the icon.
// Variants rather than classes at the call site, so every such field looks the same
// (`shadcn/no-restyle`).
const inputVariants = cva(
  'h-9 w-full min-w-0 rounded-md border border-input bg-transparent px-2.5 py-1 text-base shadow-xs transition-[color,box-shadow] outline-none file:inline-flex file:h-7 file:border-0 file:bg-transparent file:text-sm file:font-medium file:text-foreground placeholder:text-muted-foreground focus-visible:border-ring focus-visible:ring-3 focus-visible:ring-ring/50 disabled:pointer-events-none disabled:cursor-not-allowed disabled:opacity-50 aria-invalid:border-destructive aria-invalid:ring-3 aria-invalid:ring-destructive/20 md:text-sm dark:bg-input/30 dark:aria-invalid:border-destructive/50 dark:aria-invalid:ring-destructive/40',
  {
    variants: {
      font: {
        sans: '',
        mono: 'font-mono',
      },
      inset: {
        none: '',
        // Room for a size-4 icon placed at left-2.5, the way the search box draws its magnifier.
        icon: 'pl-8',
      },
    },
    defaultVariants: {
      font: 'sans',
      inset: 'none',
    },
  },
)

function Input({
  className,
  type,
  font,
  inset,
  ...props
}: React.ComponentProps<'input'> & VariantProps<typeof inputVariants>) {
  return (
    <InputPrimitive
      type={type}
      data-slot="input"
      className={cn(inputVariants({ font, inset, className }))}
      {...props}
    />
  )
}

export { Input, inputVariants }
