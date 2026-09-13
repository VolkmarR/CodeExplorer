import { defineLanguage } from '@tanstack/highlight'
import { collect } from '@/highlight/patterns'

/** Reserved words, plus the contextual keywords that read as keywords wherever they appear. */
const KEYWORDS =
  'abstract|add|and|as|async|await|base|break|case|catch|checked|class|const|continue|default|delegate|do|' +
  'else|enum|event|explicit|extern|file|finally|fixed|for|foreach|get|global|goto|if|implicit|in|init|' +
  'interface|internal|is|lock|namespace|new|not|operator|or|out|override|params|partial|private|protected|' +
  'public|readonly|record|ref|remove|required|return|scoped|sealed|set|sizeof|stackalloc|static|struct|switch|' +
  'this|throw|try|typeof|unchecked|unsafe|using|value|var|virtual|volatile|when|where|while|with|yield'

const BUILT_IN_TYPES =
  'bool|byte|char|decimal|double|dynamic|float|int|long|nint|nuint|object|sbyte|short|string|uint|ulong|ushort|void'

/**
 * C#, which `@tanstack/highlight` 0.1.0 does not ship and has no grammar engine for. Hand-written
 * patterns in the shape its own languages use, kept here rather than reaching for a second
 * highlighter (ADR-0004); a candidate for upstream contribution.
 */
export const csharp = defineLanguage({
  aliases: ['cs', 'c#'],
  name: 'csharp',
  tokenize: (code) =>
    collect(code, [
      // Comments and every string form first, so nothing inside one is tokenized as code. The order
      // within the alternation matters too: raw strings before verbatim before ordinary.
      {
        className: (match) => (match[0].startsWith('/') ? 'comment' : 'string'),
        regex:
          /\/\/[^\n]*|\/\*[\s\S]*?\*\/|"""[\s\S]*?"""|[@$]{1,2}"(?:""|[^"])*"|"(?:\\.|[^"\\\n])*"|'(?:\\.|[^'\\\n])*'/g,
      },
      // Preprocessor directives and attributes: both start a line and both describe the code rather
      // than being it. An attribute is required to start with an upper-case name, which keeps the
      // pattern off array indexers and collection expressions.
      { className: 'meta', regex: /^[ \t]*#[^\n]*/gm },
      { className: 'meta', regex: /^[ \t]*\[[A-Z][\w.]*[^\n]*\]/gm },
      { className: 'literal', regex: /\b(?:true|false|null)\b/g },
      { className: 'keyword', regex: new RegExp(String.raw`\b(?:${KEYWORDS})\b`, 'g') },
      { className: 'type', regex: new RegExp(String.raw`\b(?:${BUILT_IN_TYPES})\b`, 'g') },
      // The name a declaration introduces, coloured as a type even though the keyword before it has
      // already been claimed above: `collect` only rejects overlaps, and the group does not overlap.
      {
        className: 'type',
        group: 1,
        regex: /\b(?:class|delegate|enum|interface|record|struct)\s+([A-Za-z_]\w*)/g,
      },
      // A name an argument list follows, with a type argument list allowed in between so that
      // `Assert.IsType<Foo>(block)` reads as a call and not as two types.
      { className: 'function', regex: /\b[A-Za-z_]\w*(?=\s*(?:<[\w\s,.?[\]]*>)?\s*\()/g },
      // PascalCase after the call form, so `Console.WriteLine` reads as a type and a call rather
      // than two types. Everything else after a dot is a member access.
      { className: 'type', regex: /\b[A-Z]\w*\b/g },
      { className: 'property', group: 1, regex: /\.\s*([A-Za-z_]\w*)/g },
      {
        className: 'number',
        regex:
          /\b(?:0[xX][\da-fA-F_]+|0[bB][01_]+|\d[\d_]*(?:\.\d[\d_]*)?(?:[eE][+-]?\d+)?)[uUlLfFdDmM]{0,2}\b/g,
      },
      {
        className: 'operator',
        regex:
          /=>|\?\?=?|\?\.|\.\.|\+\+|--|<<=?|>>>?=?|[=!<>+\-*/%&|^]=|&&|\|\||[+\-*/%&|^!~<>=?:]/g,
      },
    ]),
})
