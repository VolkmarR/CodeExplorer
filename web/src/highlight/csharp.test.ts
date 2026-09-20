import { expect, test } from 'vite-plus/test'
import { highlighter } from '@/highlight/highlighter'

/**
 * The C# definition carries four comments that each explain why a pattern sits where it does. A
 * comment is not a check: reorder the list and the file still tokenizes, still renders, and is
 * simply coloured wrong. So each of those decisions gets one case that only passes in the order the
 * comment argues for — swap the two patterns it names and the assertion below it fails.
 */
function classOf(code: string, text: string): string | undefined {
  const { tokens } = highlighter.tokenize(code, { lang: 'csharp' })
  return tokens.find((token) => token.value === text)?.className
}

function classes(code: string): [string, string | null][] {
  const { tokens } = highlighter.tokenize(code, { lang: 'csharp' })
  return tokens.map((token) => [token.value, token.className ?? null])
}

test('an attribute is meta, which needs its pattern to run before names are claimed', () => {
  // Swap the attribute rule with the keyword or type rules below it and `Fact` is claimed as a type
  // first, so the attribute can no longer claim the whole bracketed span and the line reads as code.
  expect(classOf('[Fact]\npublic void A() { }', '[Fact]')).toBe('meta')
  expect(
    classOf(
      '[MethodImpl(MethodImplOptions.AggressiveInlining)]\nvoid A() { }',
      '[MethodImpl(MethodImplOptions.AggressiveInlining)]',
    ),
  ).toBe('meta')
})

/**
 * Known, and not fixed here: strings are claimed before attributes on purpose, so an attribute
 * carrying a string argument is cut open and its name reads as a call. `[Obsolete("gone")]` comes
 * back as `[`, `Obsolete` (function), `(`, `"gone"` (string), `)]`. Every attribute with a message
 * or a route template is affected, which is most of them in an ASP.NET file. Un-skip once the
 * attribute rule runs before the string rule, or is taught to survive a string inside it.
 */
test.skip('an attribute carrying a string argument is still meta', () => {
  expect(classOf('[Obsolete("gone")]\nclass A { }', '[Obsolete("gone")]')).toBe('meta')
})

test('an attribute must start upper-case, which keeps the rule off array indexers', () => {
  // The rule is anchored to the start of a line, so what the guard is for is a bracket that opens
  // one: an indexer or a collection expression wrapped onto its own line. Relax `[A-Z]` to any
  // letter and the whole line below greys out as a directive.
  const indexer = 'var value = map\n    [key] = 1;'
  expect(classes(indexer).some(([, className]) => className === 'meta')).toBe(false)
  const collection = 'var items =\n    [first, second];'
  expect(classes(collection).some(([, className]) => className === 'meta')).toBe(false)
  // An upper-case name on such a line is the price of the guard and is coloured as an attribute;
  // asserting it keeps the trade visible rather than leaving it to the comment alone.
  expect(classOf('[Fact]', '[Fact]')).toBe('meta')
})

test('the name a `record` declaration introduces is a type, not the call its parentheses look like', () => {
  // A positional record is the case: `Point(` matches the call rule too, so the declaration rule has
  // to come first. Swap the two and `Point` reads as a function.
  expect(classOf('record Point(int X, int Y);', 'Point')).toBe('type')
  expect(classOf('record class Money { }', 'Money')).toBe('type')
})

test('the name a `struct` declaration introduces is a type, primary constructor included', () => {
  expect(classOf('readonly struct Size(int W, int H);', 'Size')).toBe('type')
  expect(classOf('struct Point { }', 'Point')).toBe('type')
})

test('a generic call reads as one call and not as two types', () => {
  // The call rule allows a type argument list between the name and the parentheses, and runs before
  // the PascalCase rule. Swap those two and `IsType` is coloured as a type beside `Foo`.
  const code = 'Assert.IsType<Foo>(block);'
  expect(classOf(code, 'IsType')).toBe('function')
  expect(classOf(code, 'Foo')).toBe('type')
  expect(classOf(code, 'Assert')).toBe('type')
})

test('a call after a dot is still a call, which is what puts the call rule before PascalCase', () => {
  expect(classOf('Console.WriteLine(Size);', 'WriteLine')).toBe('function')
  expect(classOf('Console.WriteLine(Size);', 'Console')).toBe('type')
})

test('every C# token maps back to the exact source text', () => {
  // `collect` claims byte ranges, so an off-by-one in one pattern silently drops or duplicates
  // characters. Rejoining is the cheapest check that no range is wrong.
  const code =
    '[Fact]\npublic sealed record Point(int X)\n{\n    void Run() => Console.WriteLine($"{X:0.00}");\n}'
  expect(
    classes(code)
      .map(([value]) => value)
      .join(''),
  ).toBe(code)
})
