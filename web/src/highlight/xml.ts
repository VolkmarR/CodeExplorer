import { defineLanguage, type TokenRange } from '@tanstack/highlight'
import { collect } from '@/highlight/patterns'

/** An XML name: letters in any script, as a German `<Größe>` in a resource file needs. */
const NAME = String.raw`[\p{L}_][\p{L}\p{N}_:.\-]*`

/**
 * Everything that sits beside an element, and the element tags themselves, in one alternation.
 * One pattern rather than one per form, because they nest inside each other's text: a `<!--` inside
 * a CDATA section, or a `<![CDATA[` inside a comment. Scanning once from the left lets whichever
 * opens first claim its span, which is what keeps the other one text.
 *
 * A tag's body steps over quoted values one at a time, so a `>` inside an attribute — an MSBuild
 * version condition — does not end the tag. A DOCTYPE may carry an internal subset in brackets.
 */
const OUTSIDE_A_TAG = new RegExp(
  [
    String.raw`<!--[\s\S]*?(?:-->|$)`,
    String.raw`<!\[CDATA\[[\s\S]*?(?:\]\]>|$)`,
    String.raw`<\?[\s\S]*?(?:\?>|$)`,
    String.raw`<![A-Za-z][^[>]*(?:\[[\s\S]*?\][^>]*)?>?`,
    String.raw`<\/?${NAME}(?:[^<>"']|"[^"]*"|'[^']*')*>`,
  ].join('|'),
  'gu',
)

/**
 * What one tag is made of, again in one left-to-right alternation, so an attribute name is never
 * looked for inside a value. A value is quoted with no escapes, so `"bin\"` ends at its second quote;
 * the HTML grammar reads `\"` as an escape and ran such a value on into the rest of the file.
 */
const INSIDE_A_TAG = new RegExp(
  String.raw`("[^"]*"|'[^']*')|(?<=^<\/?)(${NAME})|(${NAME})(?=\s*=)`,
  'gu',
)

function classOf(span: string): TokenRange['className'] {
  if (span.startsWith('<!--')) return 'comment'
  if (span.startsWith('<![CDATA[')) return 'string'
  if (span.startsWith('<?') || span.startsWith('<!')) return 'meta'
  // A placeholder only: `tokenize` replaces a whole tag with the pieces inside it.
  return 'tag'
}

/** The name, attribute names and values of the tag at `[start, end)`. */
function insideTag(code: string, start: number, end: number): TokenRange[] {
  const tag = code.slice(start, end)
  const ranges: TokenRange[] = []
  INSIDE_A_TAG.lastIndex = 0
  let match: RegExpExecArray | null
  while ((match = INSIDE_A_TAG.exec(tag)) !== null) {
    const className = match[1] ? 'string' : match[2] ? 'tag' : 'attr'
    ranges.push({
      className,
      end: start + match.index + match[0].length,
      start: start + match.index,
    })
  }
  return ranges
}

/**
 * XML, for the project, resource and configuration files a .NET repository is full of. The bundled
 * `html` grammar was tried first and lists `xml` as an alias, but it colours a `<tag>` inside a CDATA
 * section as an element, leaves the `<?xml?>` declaration uncoloured and reads a backslash before a
 * closing quote as an escape. Hand-written in the shape of the C# definition instead (ADR-0004).
 */
export const xml = defineLanguage({
  name: 'xml',
  tokenize: (code) =>
    collect(code, [{ className: (match) => classOf(match[0]), regex: OUTSIDE_A_TAG }]).flatMap(
      (range) => (range.className === 'tag' ? insideTag(code, range.start, range.end) : [range]),
    ),
})
