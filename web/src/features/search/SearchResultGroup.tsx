import { Link } from '@tanstack/react-router'
import { FileCode } from 'lucide-react'
import { fileSearch } from '@/lib/urls/fileParams'
import { MatchedLine } from '@/features/search/MatchedLine'
import { matchRanges } from '@/features/search/matchRanges'
import { languageFor } from '@/highlight/highlighter'
import type { GrepFile } from '@/features/search/api'
import { directoryOf, fileName, formatCount } from '@/lib/format'

/** One shared empty array for context lines, so a row without marks does not get a new prop each render. */
const NO_MATCHES: never[] = []

/**
 * One file's matches: its name, what it is, and the lines that matched, nested inside the page's
 * card rather than floating on the background.
 *
 * Ours rather than a shadcn component, because nothing in the library is this: a header that is a
 * link, a body that is a table of code, and a line number that is a second link into the file. It
 * composes what the library does have and writes only the part that is about code.
 */
export function SearchResultGroup({
  project,
  file,
  pattern,
}: {
  project: string
  file: GrepFile
  pattern: RegExp | null
}) {
  const language = languageFor(file.qualifiedPath)

  return (
    <div className="overflow-hidden rounded-lg border">
      <div className="flex flex-wrap items-baseline gap-x-3 gap-y-1 border-b bg-muted/40 px-3 py-2">
        <FileCode aria-hidden="true" className="size-4 self-center text-muted-foreground" />
        <Link
          to="/projects/$project/file"
          params={{ project }}
          search={fileSearch(file.qualifiedPath, undefined, { view: 'search' })}
          className="min-w-0 font-mono text-sm hover:underline"
        >
          {/* The directory is dimmed and the name is not: a page of results is scanned by file
              name, and the repository and folder are there to tell two of the same name apart. */}
          <span className="text-muted-foreground">{directoryOf(file.qualifiedPath)}</span>
          {fileName(file.qualifiedPath)}
        </Link>
        <span className="ml-auto shrink-0 text-xs text-muted-foreground tabular-nums">
          {formatCount(file.matchCount)} {file.matchCount === 1 ? 'match' : 'matches'}
          {file.matchesShown < file.matchCount ? `, showing ${file.matchesShown}` : ''}
        </span>
      </div>
      <table className="w-full border-collapse font-mono text-xs">
        <tbody>
          {file.lines.map((line) => (
            <tr key={line.lineNumber} className={line.isMatch ? 'bg-primary/10' : undefined}>
              <td className="w-14 shrink-0 border-r px-2 py-0.5 text-right text-muted-foreground/70 select-none">
                <Link
                  to="/projects/$project/file"
                  params={{ project }}
                  search={fileSearch(file.qualifiedPath, line.lineNumber, { view: 'search' })}
                  className="hover:text-primary hover:underline"
                >
                  {line.lineNumber}
                </Link>
              </td>
              <td className="overflow-x-auto px-3 py-0.5 whitespace-pre">
                <MatchedLine
                  text={line.text}
                  language={language}
                  // Context lines carry no match by the server's word, so they are not searched
                  // again: a context line can contain the query without counting.
                  ranges={line.isMatch ? matchRanges(line.text, pattern) : NO_MATCHES}
                />
              </td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  )
}
