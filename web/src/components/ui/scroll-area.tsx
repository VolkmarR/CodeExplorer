'use client'

import { ScrollArea as ScrollAreaPrimitive } from '@base-ui/react/scroll-area'
import { cn } from '@/lib/utils'

function ScrollArea({ className, children, ...props }: ScrollAreaPrimitive.Root.Props) {
  return (
    <ScrollAreaPrimitive.Root
      data-slot="scroll-area"
      className={cn('relative', className)}
      {...props}
    >
      <ScrollAreaPrimitive.Viewport
        data-slot="scroll-area-viewport"
        // `max-h-[inherit]` is what makes a bound on the root bound the thing that actually scrolls.
        // The viewport is sized `h-full`, a percentage that resolves against an auto height when the
        // root carries only a `max-height` — so the viewport grew with its content and never
        // overflowed, and a long file scrolled the page instead of the pane. Inheriting the root's
        // computed `max-height` fixes it here, once, rather than asking every caller to know that a
        // height goes on the viewport and not on the root.
        className="size-full max-h-[inherit] rounded-[inherit] transition-[color,box-shadow] outline-none focus-visible:ring-[3px] focus-visible:ring-ring/50 focus-visible:outline-1"
      >
        {children}
      </ScrollAreaPrimitive.Viewport>
      <ScrollBar />
      {/* Both axes. The one pane in this app that needs it — the code view — scrolls sideways as
          well as down, and a native bar there would not match the styled one above it. */}
      <ScrollBar orientation="horizontal" />
      <ScrollAreaPrimitive.Corner />
    </ScrollAreaPrimitive.Root>
  )
}

function ScrollBar({
  className,
  orientation = 'vertical',
  ...props
}: ScrollAreaPrimitive.Scrollbar.Props) {
  return (
    <ScrollAreaPrimitive.Scrollbar
      data-slot="scroll-area-scrollbar"
      data-orientation={orientation}
      orientation={orientation}
      className={cn(
        // `data-[orientation=…]` and not Tailwind's `data-vertical:` / `data-horizontal:`
        // shorthands, which compile to `[data-vertical]` and `[data-horizontal]`: Base UI marks a
        // scrollbar with `data-orientation="vertical"`, so those attributes never exist and every
        // rule under them was dropped. The bar was left with its 1px of padding and a thumb of no
        // width at all — a scrollbar that takes up space, responds to nothing and cannot be seen.
        // It went unnoticed because the pane it belongs to did not overflow either (CodeView).
        'flex touch-none p-px transition-colors select-none data-[orientation=horizontal]:h-2.5 data-[orientation=horizontal]:flex-col data-[orientation=horizontal]:border-t data-[orientation=horizontal]:border-t-transparent data-[orientation=vertical]:h-full data-[orientation=vertical]:w-2.5 data-[orientation=vertical]:border-l data-[orientation=vertical]:border-l-transparent',
        className,
      )}
      {...props}
    >
      <ScrollAreaPrimitive.Thumb
        data-slot="scroll-area-thumb"
        className="relative flex-1 rounded-full bg-border"
      />
    </ScrollAreaPrimitive.Scrollbar>
  )
}

export { ScrollArea, ScrollBar }
