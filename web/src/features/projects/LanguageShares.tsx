import type { CSSProperties } from 'react'
import type { IndexOverview, LanguageShare } from '@/features/projects/api'
import { ExcludedNote } from '@/features/projects/ExcludedNote'
import { formatCount } from '@/lib/format'
import { Badge } from '@/components/ui/badge'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'

/** What the project is written in, by lines, largest first. */
export function LanguageShares({
  project,
  overview,
  excluded,
}: {
  project: string
  overview: IndexOverview
  /** Files at HEAD the excluded paths left out, if any were. */
  excluded: number | undefined
}) {
  const total = overview.languages.reduce((sum, language) => sum + language.lines, 0)
  return (
    <Card>
      <CardHeader>
        <CardTitle>Languages</CardTitle>
      </CardHeader>
      <CardContent className="space-y-2">
        {overview.languages.map((language) => (
          <LanguageBar key={language.name} language={language} total={total} />
        ))}
        {overview.otherLanguages > 0 ? (
          <p className="text-xs text-muted-foreground">
            and {formatCount(overview.otherLanguages)} more
          </p>
        ) : null}
        <ExcludedNote project={project} files={excluded} what="the files at HEAD" />
      </CardContent>
    </Card>
  )
}

/**
 * A bar rather than a percentage on its own: the shares are read against each other, and the widest
 * bar answers "what is this project written in" before any number is read.
 */
function LanguageBar({ language, total }: { language: LanguageShare; total: number }) {
  const share = total === 0 ? 0 : (language.lines / total) * 100
  return (
    <div className="space-y-1">
      <div className="flex items-baseline justify-between gap-3 text-sm">
        <span className="flex items-baseline gap-2 truncate">
          {language.name}
          {/* An extension standing for itself is a weaker claim than a language name, and has to
              read as one: "X#" is a fact about the file, ".vh" is only what it is called. */}
          {language.mapped ? null : <Badge variant="muted">extension</Badge>}
        </span>
        <span className="shrink-0 tabular-nums text-muted-foreground">
          {formatCount(language.files)} files &middot; {formatCount(language.lines)} lines
        </span>
      </div>
      <div className="h-1.5 rounded-full bg-muted">
        <div
          className="h-1.5 w-(--share) rounded-full bg-primary"
          style={{ '--share': `${share}%` } as CSSProperties}
        />
      </div>
    </div>
  )
}
