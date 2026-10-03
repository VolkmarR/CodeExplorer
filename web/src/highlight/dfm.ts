import { defineLanguage } from '@tanstack/highlight'
import { collect } from '@/highlight/patterns'

/**
 * The words that open and close a block, only where a block starts: at the head of a line and not
 * followed by `=` or `.`, so a property that happens to be called `Item` or `End` stays a property.
 * Delphi is case-insensitive, so every pattern that reads a word carries the `i` flag.
 */
const KEYWORD = /^[ \t]*(object|inherited|inline|item|end)\b(?![ \t]*[=.])/gim

/**
 * Delphi form files (`.dfm`) in their text form, which `@tanstack/highlight` has no grammar for.
 * Same hand-written shape as the C# definition (ADR-0004).
 *
 * The older binary form, starting `TPF0`, is classified as binary by the index and never reaches the
 * file view, so it is not handled here.
 *
 * Identifier values (`True`, `clBtnFace`, set members such as `fsBold`) are left uncoloured: they
 * are the bulk of a form, and colouring them would leave nothing to stand out.
 */
export const dfm = defineLanguage({
  name: 'dfm',
  tokenize: (code) =>
    collect(code, [
      // Strings, `#13`-style character codes and binary blocks first, so a keyword inside a caption
      // stays text and a bitmap's hex digits are one muted span rather than a stream of numbers.
      // One alternation, scanned from the left, because each can hold the other's opening
      // character: a caption `'Open {'` must not start a binary block that swallows the next one.
      // A string never spans lines; a long one is split into pieces joined with `+`.
      {
        className: (match) => (match[0].startsWith('{') ? 'comment' : 'string'),
        regex: /'(?:''|[^'\n])*'|#(?:\d+|\$[\da-f]+)|\{[^}]*\}?/gi,
      },
      { className: 'keyword', group: 1, regex: KEYWORD },
      // The class a block instantiates. The component name before the colon is optional.
      {
        className: 'type',
        group: 1,
        regex: /^[ \t]*(?:object|inherited|inline)[ \t]+(?:\w+[ \t]*:[ \t]*)?([\w.]+)/gim,
      },
      { className: 'property', group: 1, regex: /^[ \t]*([a-z_][\w.]*)(?=[ \t]*=)/gim },
      {
        className: 'number',
        regex: /\$[\da-f]+\b|(?<![\w.$])-?\d+(?:\.\d+)?(?:e[+-]?\d+)?\b/gi,
      },
    ]),
})
