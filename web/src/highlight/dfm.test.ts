import { expect, test } from 'vite-plus/test'
import { highlighter } from '@/highlight/highlighter'

/**
 * A Delphi form in its text form: nested components, each a list of `Prop = value` lines. The value
 * forms are what a pattern can confuse — a set, a string list, a collection and a binary block each
 * hold text that would read as something else on its own.
 */
function classes(code: string): [string, string | null][] {
  const { tokens } = highlighter.tokenize(code, { lang: 'dfm' })
  return tokens.map((token) => [token.value, token.className ?? null])
}

function classOf(code: string, text: string): string | null | undefined {
  return classes(code).find(([value]) => value === text)?.[1]
}

const FORM = [
  'object MainForm: TMainForm',
  '  Left = -8',
  "  Caption = #39'Kunden'#39' end'#13#10'object'",
  '  Color = clBtnFace',
  '  Font.Style = [fsBold, fsItalic]',
  '  Font.Height = $FF00',
  '  ScaleFactor = 1.25',
  '  inherited Panel1: TPanel',
  '    Items.Strings = (',
  "      'eins'",
  "      'zwei')",
  '    Columns = <',
  '      item',
  '        Width = 120',
  '      end>',
  '    Picture.Data = {',
  '      0A54426974',
  '      6D61700100}',
  '  end',
  'end',
].join('\n')

test('DFM colours the keywords that open and close a block', () => {
  const keywords = classes(FORM).filter(([, className]) => className === 'keyword')
  expect(keywords.map(([value]) => value)).toEqual([
    'object',
    'inherited',
    'item',
    'end',
    'end',
    'end',
  ])
})

test('the class after the colon is a type and the component name is not', () => {
  expect(classOf(FORM, 'TMainForm')).toBe('type')
  expect(classOf(FORM, 'TPanel')).toBe('type')
  expect(classOf(FORM, 'MainForm')).toBeUndefined()
})

test('a property name is a property, dotted names whole', () => {
  expect(classOf(FORM, 'Left')).toBe('property')
  expect(classOf(FORM, 'Font.Style')).toBe('property')
  expect(classOf(FORM, 'Items.Strings')).toBe('property')
  expect(classOf(FORM, 'Width')).toBe('property')
})

test('strings and character codes are strings, and a keyword inside a string stays text', () => {
  expect(classOf(FORM, "'Kunden'")).toBe('string')
  expect(classOf(FORM, "' end'")).toBe('string')
  expect(classOf(FORM, '#13')).toBe('string')
  expect(classOf(FORM, '#10')).toBe('string')
  expect(classOf(FORM, "'object'")).toBe('string')
  expect(classOf(FORM, "'eins'")).toBe('string')
  expect(classOf("  Hint = 'Don''t'", "'Don''t'")).toBe('string')
})

test('integers, floats and hex are numbers', () => {
  expect(classOf(FORM, '-8')).toBe('number')
  expect(classOf(FORM, '$FF00')).toBe('number')
  expect(classOf(FORM, '1.25')).toBe('number')
  expect(classOf(FORM, '120')).toBe('number')
})

test('identifier values and set members are left uncoloured', () => {
  expect(classOf(FORM, 'clBtnFace')).toBeUndefined()
  expect(classOf(FORM, 'fsBold')).toBeUndefined()
})

test('a binary block is one muted span, never a stream of numbers', () => {
  expect(classOf(FORM, '{\n      0A54426974\n      6D61700100}')).toBe('comment')
  expect(classes(FORM).some(([value]) => value === '0A54426974')).toBe(false)
})

test('a brace inside a string does not open a binary block', () => {
  const code = "  Caption = 'Open {'\n  Picture.Data = {\n    0123456789\n    0A0B}"
  expect(classOf(code, "'Open {'")).toBe('string')
  expect(classOf(code, '{\n    0123456789\n    0A0B}')).toBe('comment')
})

test('DFM keywords are recognised in any case, as Delphi is case-insensitive', () => {
  const code = 'OBJECT Form1: TForm1\n  Inherited Btn: TButton\n  END\nEnd'
  const keywords = classes(code).filter(([, className]) => className === 'keyword')
  expect(keywords.map(([value]) => value)).toEqual(['OBJECT', 'Inherited', 'END', 'End'])
  expect(classOf(code, 'TButton')).toBe('type')
})

test('an inline frame and an inherited child index are read too', () => {
  const code = 'inline Frame1: TFrame1 [2]\nend'
  expect(classOf(code, 'inline')).toBe('keyword')
  expect(classOf(code, 'TFrame1')).toBe('type')
  expect(classOf(code, '2')).toBe('number')
})

test('every DFM token maps back to the exact source text', () => {
  expect(
    classes(FORM)
      .map(([value]) => value)
      .join(''),
  ).toBe(FORM)
})
