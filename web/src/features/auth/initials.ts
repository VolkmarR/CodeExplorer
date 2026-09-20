/**
 * At most two letters from the name the tenant gave. It is a display name and not a structured
 * one — "Volkmar Rigo", "rigo.volkmar", a UPN — so this takes the first letter of the first two
 * words and gives up gracefully rather than pretending to parse a person.
 */
export function initials(name: string | null): string {
  if (!name) return '?'
  const words = name.split(/[\s.@_-]+/).filter(Boolean)
  return words
    .slice(0, 2)
    .map((word) => word[0]?.toUpperCase() ?? '')
    .join('')
}
