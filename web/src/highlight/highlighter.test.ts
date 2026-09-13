import { expect, test } from 'vite-plus/test'
import { highlighter, languageFor } from '@/highlight/highlighter'

/**
 * The two language definitions this repo owns are the only code here that could be wrong in a way
 * nothing else notices: a bad pattern mis-colours a file rather than throwing. These assert the
 * cases that pattern order decides, which is where such a definition actually goes wrong.
 */
function classOf(code: string, lang: string, text: string): string | undefined {
  const { tokens } = highlighter.tokenize(code, { lang })
  return tokens.find((token) => token.value === text)?.className
}

test('the extension decides the language, and an unknown one is plaintext', () => {
  expect(languageFor('main/src/Api/Program.cs')).toBe('csharp')
  expect(languageFor('main/Source/Start.prg')).toBe('xsharp')
  expect(languageFor('main/Source/Header.xh')).toBe('xsharp')
  expect(languageFor('main/README')).toBe('plaintext')
  expect(languageFor('main/.gitignore')).toBe('plaintext')
})

test('C# colours keywords, types, calls and members apart', () => {
  const code = 'public sealed class Widget\n{\n    void Run() => Console.WriteLine(Size);\n}'
  expect(classOf(code, 'csharp', 'public')).toBe('keyword')
  expect(classOf(code, 'csharp', 'Widget')).toBe('type')
  expect(classOf(code, 'csharp', 'Console')).toBe('type')
  expect(classOf(code, 'csharp', 'WriteLine')).toBe('function')
  expect(classOf(code, 'csharp', 'void')).toBe('type')
})

test('C# keeps code inside a comment or a string uncoloured', () => {
  // This is the one thing pattern order exists to get right.
  expect(classOf('// class Widget', 'csharp', '// class Widget')).toBe('comment')
  expect(classOf('var s = "class Widget";', 'csharp', '"class Widget"')).toBe('string')
  expect(classOf('var s = @"c:\\class";', 'csharp', '@"c:\\class"')).toBe('string')
  expect(classOf('var s = $"a {b} class";', 'csharp', '$"a {b} class"')).toBe('string')
})

test('X# keywords are case-insensitive, as the language is', () => {
  for (const written of ['FUNCTION', 'Function', 'function']) {
    expect(classOf(`${written} Start() AS VOID`, 'xsharp', written)).toBe('keyword')
  }
  expect(classOf('LOCAL n AS INT', 'xsharp', 'INT')).toBe('type')
  expect(classOf('LOCAL u AS USUAL', 'xsharp', 'USUAL')).toBe('type')
})

test('X# reads its own comment, literal and send forms', () => {
  expect(classOf('x := 1 && a comment', 'xsharp', '&& a comment')).toBe('comment')
  expect(classOf('* a Clipper comment', 'xsharp', '* a Clipper comment')).toBe('comment')
  expect(classOf('lOk := .T.', 'xsharp', '.T.')).toBe('literal')
  expect(classOf('IF a .AND. b', 'xsharp', '.AND.')).toBe('keyword')
  expect(classOf('cName := oCustomer:Name', 'xsharp', 'Name')).toBe('property')
  expect(classOf('x := #Symbol', 'xsharp', '#Symbol')).toBe('literal')
  expect(classOf('x := 1', 'xsharp', ':=')).toBe('operator')
})

test('X# names what a declaration introduces', () => {
  expect(classOf('CLASS Customer INHERIT Person', 'xsharp', 'Customer')).toBe('type')
  expect(classOf('METHOD Save() AS LOGIC', 'xsharp', 'Save')).toBe('function')
})

test('every token maps back to the exact source text', () => {
  // `collect` claims byte ranges, so an off-by-one in one pattern silently drops or duplicates
  // characters. Rejoining the tokens is the cheapest check that no range is wrong.
  const code =
    'FUNCTION Start() AS VOID\n    LOCAL i AS INT\n    i := 0x1F + 2.5\n    ? "done" // said\nRETURN'
  const { tokens } = highlighter.tokenize(code, { lang: 'xsharp' })
  expect(tokens.map((token) => token.value).join('')).toBe(code)
})
