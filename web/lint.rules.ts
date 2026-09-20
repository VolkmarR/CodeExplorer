import { REACT_DOCTOR_RULE_REGISTRY } from 'oxlint-plugin-react-doctor/core'

/**
 * React Doctor ships 906 rules and no preset, so every one has to be named in the config. Listing
 * them by hand would be a thousand-line file nobody could keep current across a version bump, so the
 * list is derived from the plugin's own registry, at the severity each rule declares for itself.
 *
 * Two groups are left out. Rules for a framework this app does not use (React Native, Next.js,
 * Preact, TanStack Start) can only produce false positives. Scan and project rules need whole-project
 * analysis, which the `react-doctor` CLI does and standalone oxlint does not, so enabling them here
 * would report from a view of one file at a time.
 */
const KEPT_FRAMEWORKS = new Set(['global', 'tanstack-query'])

/**
 * Rules that argue with a decision this project has already made, each switched off for that reason
 * rather than because it was noisy.
 */
const OVERRIDDEN: Record<string, 'off'> = {
  // Tailwind utility classes on shadcn components are the styling model (ADR-0004). This rule wants
  // a component API that does not take `className`, which is the opposite of how shadcn is written.
  'react-doctor/forbid-component-props': 'off',
  // Deep JSX is what JSX looks like; the limit is two levels.
  'react-doctor/jsx-max-depth': 'off',
  // Inline handlers and the arrays and objects built in a render are exactly what React Compiler
  // memoizes, so these would ask
  // for the hand-written memoization that enabling the compiler was meant to remove. `render` is
  // also Base UI's composition API — `<Button render={<Link />}>` is how the library says to do it,
  // where Radix took `asChild` and a child element.
  'react-doctor/jsx-no-jsx-as-prop': 'off',
  'react-doctor/jsx-no-new-array-as-prop': 'off',
  'react-doctor/jsx-no-new-function-as-prop': 'off',
  'react-doctor/jsx-no-new-object-as-prop': 'off',
  // `{...props}` is how every shadcn component forwards to its element.
  'react-doctor/jsx-props-no-spreading': 'off',
  // shadcn exports a component and its `cva` variants from one file, and a TanStack route file
  // exports `Route`. Both are conventions of libraries this project chose; the cost is a Fast
  // Refresh state reset in development.
  'react-doctor/only-export-components': 'off',
  // `jsx: 'react-jsx'` makes the compiler import the factory itself.
  'react-doctor/react-in-jsx-scope': 'off',
}

export const reactDoctorRules: Record<string, 'error' | 'warn' | 'off'> = { ...OVERRIDDEN }

for (const [id, rule] of Object.entries(REACT_DOCTOR_RULE_REGISTRY)) {
  const key = `react-doctor/${id}`
  if (key in OVERRIDDEN) continue
  if (!KEPT_FRAMEWORKS.has(rule.framework) || rule.isScanRule) continue
  reactDoctorRules[key] = rule.severity === 'error' ? 'error' : 'warn'
}
