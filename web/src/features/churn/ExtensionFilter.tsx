import type { ChurnedExtension } from '@/features/churn/api'
import { ToggleGroup, ToggleGroupItem } from '@/components/ui/toggle-group'
import { formatCount } from '@/lib/format'

/**
 * Which extensions the ranking is worth reading, as a pressed toggle each.
 *
 * The options are what the window actually holds rather than a list this app carries (#161): a
 * project whose code is `.prg` is exactly the one where a hard-coded menu of `.cs` and `.ts` offers
 * nothing, and the server already had to group the window to rank it. They come ranked by commits,
 * so what is drowning the ranking sits at the front of the row that fixes it.
 *
 * Toggles and not a select, because more than one is an answer and because the counts beside them
 * are the reason to press one: `.xlf 412c/9f` says in four characters that nine translation files
 * account for four hundred commits, which is the sentence a reader came to the page to discover.
 *
 * Nothing pressed is every extension, not none — the ranking a reader opens before filtering.
 *
 * Nothing at all where the window holds one kind of file: that is a project with no choice to make
 * rather than a filter someone forgot to use, and the decision sits here with the control instead of
 * in the page, which would then hold a condition about a thing it does not draw.
 */
export function ExtensionFilter({
  extensions,
  value,
  onChange,
}: {
  extensions: ChurnedExtension[]
  /** The chosen extensions, comma-separated as the URL carries them. */
  value: string
  onChange: (extensions: string) => void
}) {
  if (extensions.length < 2) return null

  return (
    <div className="space-y-1.5">
      <p className="text-xs text-muted-foreground">
        Rank only these extensions — the ones this window changed, most commits first
      </p>
      <Toggles extensions={extensions} value={value} onChange={onChange} />
    </div>
  )
}

/** The row of toggles itself, so the component above is the decision and this one is the drawing. */
function Toggles({
  extensions,
  value,
  onChange,
}: {
  extensions: ChurnedExtension[]
  value: string
  onChange: (extensions: string) => void
}) {
  const chosen = split(value)

  return (
    <ToggleGroup
      size="sm"
      variant="outline"
      // Base UI's group is single-select by default, and here that would make each extension the
      // alternative to every other — where the question is which SET of them is the code.
      multiple
      value={chosen}
      onValueChange={(next: string[]) => onChange(next.join(','))}
      // Wrapping, because a window with twenty extensions is normal and a row that scrolled
      // sideways would hide exactly the long tail this control exists to let someone skip.
      className="flex-wrap"
      aria-label="Extensions to rank"
    >
      {extensions.map((extension) => (
        <ToggleGroupItem
          key={extension.extension}
          value={extension.extension}
          // The empty extension is a real row — a Makefile, a LICENSE — and cannot be pressed,
          // because there is no term that selects it; it is here so the counts add up on screen.
          disabled={extension.extension === ''}
          className="font-mono text-xs"
        >
          {extension.extension === '' ? (
            <span className="font-sans">no extension</span>
          ) : (
            extension.extension
          )}
          <span className="ml-1.5 tabular-nums text-muted-foreground">
            {formatCount(extension.commits)}
          </span>
        </ToggleGroupItem>
      ))}
    </ToggleGroup>
  )
}

/** The URL's comma-separated list as the toggles' array. Empty is every extension, so no terms. */
function split(value: string): string[] {
  return value === '' ? [] : value.split(',')
}
