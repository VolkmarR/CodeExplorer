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
 * Strings and comments are claimed before attributes and directives on purpose, so those two rules
 * fill what is left of their span rather than claiming all of it (`patterns.ts`, `fill`). Before
 * that, an attribute carrying a string was cut open and its name read as a call.
 */
test('an attribute carrying a string argument is still meta, and the string is still a string', () => {
  // Not one token: the string was claimed first and keeps its own colour, so the attribute fills
  // what is left around it. That is what an editor shows, and it is why the rule fills instead of
  // claiming the whole span — all-or-nothing gave the entire attribute up over the four characters
  // of `"gone"`, and `Obsolete` then read as a call.
  expect(classes('[Obsolete("gone")]\nclass A { }')).toEqual(
    expect.arrayContaining([
      ['[Obsolete(', 'meta'],
      ['"gone"', 'string'],
      [')]', 'meta'],
    ]),
  )
  expect(classOf('[Obsolete("gone")]\nclass A { }', 'Obsolete')).toBeUndefined()
  // The route template an ASP.NET file is full of, which was the case that made this worth fixing.
  expect(classes('[Route("api/projects/{slug}")]\nclass C { }')).toEqual(
    expect.arrayContaining([
      ['[Route(', 'meta'],
      ['"api/projects/{slug}"', 'string'],
      [')]', 'meta'],
    ]),
  )
})

test('a preprocessor directive keeps its colour when a comment follows it on the line', () => {
  // The same defect as the attribute above: the comment is claimed first, and before the directive
  // rule filled, the whole `#pragma` gave way to it and `warning` read as a keyword.
  expect(classes('#pragma warning disable CS0618 // obsolete on purpose')).toEqual(
    expect.arrayContaining([
      ['#pragma warning disable CS0618 ', 'meta'],
      ['// obsolete on purpose', 'comment'],
    ]),
  )
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
