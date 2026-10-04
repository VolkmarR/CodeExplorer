// Imported so this file is a module, which makes the block below an augmentation of React's own
// types rather than a declaration that would replace them.
import 'react'

/**
 * A CSS custom property is a style React passes through as written, and how this app carries a
 * runtime value to a Tailwind class: `style={{ '--share': '40%' }}` beside `w-(--share)`, so every
 * style stays a class the design-system lint can read. React's own type lists only the standard
 * properties, which left each of those objects needing an assertion to `CSSProperties` that the
 * linter rightly calls unsafe. Declared once here instead, so the compiler checks them.
 */
declare module 'react' {
  interface CSSProperties {
    [property: `--${string}`]: string | number | undefined
  }
}
