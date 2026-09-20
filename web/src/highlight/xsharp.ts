import { defineLanguage } from '@tanstack/highlight'
import { collect } from '@/highlight/patterns'

/**
 * Statement and declaration keywords, including the `END`/`NEXT` family that closes them. X# is
 * case-insensitive and written in every case there is, so every pattern here carries the `i` flag
 * rather than listing `IF`, `If` and `if`.
 */
const KEYWORDS =
  'access|align|as|assign|begin|break|case|catch|class|clipper|constructor|declare|default|define|delegate|' +
  'destructor|dim|do|downto|else|elseif|end|endcase|endclass|enddefine|enddo|endif|endtry|enum|event|exit|' +
  'export|fastcall|field|finally|for|foreach|function|global|hidden|if|implements|implied|in|inherit|init|' +
  'instance|interface|internal|is|local|loop|member|method|namespace|next|operator|otherwise|out|param|' +
  'parameters|partial|pascal|private|procedure|property|protected|public|recover|ref|repeat|return|sealed|' +
  'self|seq|sequence|set|static|step|strict|structure|super|switch|thiscall|throw|to|try|until|upto|using|' +
  'var|virtual|vostruct|while|winapi|yield'

/**
 * The xBase types and their .NET-facing aliases. `usual`, `symbol` and `array` are the ones that
 * mark a file as X# rather than C#.
 */
const TYPES =
  'array|binary|byte|char|codeblock|currency|date|datetime|dword|decimal|dynamic|float|int|int64|logic|long|' +
  'object|psz|ptr|real4|real8|short|string|symbol|uint64|usual|void|word'

/**
 * X#, for the `.prg` sources this repository's neighbours are written in. Same hand-written shape as
 * the C# definition, and the same reasoning behind it (ADR-0004): a language `@tanstack/highlight`
 * lacks gets a definition here rather than a second highlighter.
 *
 * Two X# forms are deliberately not recognised. `[...]` is both a string literal and an array index
 * and cannot be told apart without parsing, so it is left alone rather than colouring every index as
 * a string. A leading `*` comment is only a comment in the first column of a line, which the comment
 * pattern anchors for.
 */
export const xsharp = defineLanguage({
  aliases: ['xs', 'x#', 'prg'],
  name: 'xsharp',
  tokenize: (code) =>
    collect(code, [
      // Four comment forms: `//`, `/* */`, `&&` to end of line, and a `*` in the first column. Then
      // the two unambiguous string forms. Strings first within each group, as in the C# definition.
      {
        className: 'comment',
        regex: /\/\/[^\n]*|\/\*[\s\S]*?\*\/|&&[^\n]*|^[ \t]*\*[^\n]*/gm,
      },
      { className: 'string', regex: /e?"(?:\\.|[^"\\\n])*"|'(?:\\.|[^'\\\n])*'/g },
      // Preprocessor directives, which in X# also include `#command` and `#translate`. Both these
      // rules fill rather than claim the whole span, for the reason the C# definition gives: the
      // strings and comments inside them are already claimed, and all-or-nothing would hand back
      // the entire directive over them. `#include "foo.ch"` is the common case here.
      {
        className: 'meta',
        fill: true,
        regex:
          /^[ \t]*#(?:command|define|else|endif|endregion|ifn?def|include|region|translate|using|xcommand|xtranslate)\b[^\n]*/gim,
      },
      // `[Foo]` at the start of a line is an attribute; anywhere else it is an array index.
      { className: 'meta', fill: true, regex: /^[ \t]*\[[A-Za-z_][\w.]*[^\n]*\]/gm },
      // `#SYMBOL` is a literal of its own in X#, not a preprocessor directive — those are anchored to
      // the start of a line above. It has to be claimed before the keyword and type patterns, or
      // `#Symbol` loses its name half to the `SYMBOL` type and the `#` is left stranded.
      { className: 'literal', regex: /#[A-Za-z_]\w*/g },
      // The xBase logicals and null. `.T.`/`.F.` are literals; `.AND.`/`.OR.`/`.NOT.` are operators
      // spelled the same way, and are claimed as keywords below.
      { className: 'literal', regex: /\.[tTfF]\.|\b(?:true|false|nil|null)\b/g },
      { className: 'keyword', regex: /\.(?:and|or|not)\./gi },
      { className: 'keyword', regex: new RegExp(String.raw`\b(?:${KEYWORDS})\b`, 'gi') },
      { className: 'type', regex: new RegExp(String.raw`\b(?:${TYPES})\b`, 'gi') },
      // The name a declaration introduces. `function`/`method` name a callable, the rest name a type.
      {
        className: 'function',
        group: 1,
        regex: /\b(?:function|procedure|method|access|assign)\s+([A-Za-z_]\w*)/gi,
      },
      {
        className: 'type',
        group: 1,
        regex: /\b(?:class|define|delegate|enum|interface|structure|vostruct)\s+([A-Za-z_]\w*)/gi,
      },
      { className: 'function', regex: /\b[A-Za-z_]\w*(?=\s*(?:<[\w\s,.[\]]*>)?\s*\()/g },
      // `:` is the send operator — `oCustomer:Name` — so members are found after a colon as well as
      // after a dot. The colon form comes first because it is the one that is unambiguous.
      { className: 'property', group: 1, regex: /(?<![:=]):(?!=)\s*([A-Za-z_]\w*)/g },
      { className: 'property', group: 1, regex: /\.\s*([A-Za-z_]\w*)/g },
      {
        className: 'number',
        regex: /\b(?:0[xX][\da-fA-F]+|\d+(?:\.\d+)?(?:[eE][+-]?\d+)?)[uUlLsS]?\b/g,
      },
      // `:=` is assignment, `==` exact comparison, `<>` and `#` inequality, `->` the alias operator.
      {
        className: 'operator',
        regex: /:=|==|<>|->|\+\+|--|<<=?|>>=?|[=!<>+\-*/%^]=|\*\*|[+\-*/%<>=^]/g,
      },
    ]),
})
