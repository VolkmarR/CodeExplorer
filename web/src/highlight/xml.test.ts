import { expect, test } from 'vite-plus/test'
import { highlighter } from '@/highlight/highlighter'

/**
 * XML is the one markup this view gets that is not HTML: project files, resources and configuration.
 * What matters is what the HTML grammar gets wrong on it — a `<tag>` inside a comment or a CDATA
 * section is text, not an element, and the declaration is not a tag.
 */
function classes(code: string): [string, string | null][] {
  const { tokens } = highlighter.tokenize(code, { lang: 'xml' })
  return tokens.map((token) => [token.value, token.className ?? null])
}

function classOf(code: string, text: string): string | null | undefined {
  return classes(code).find(([value]) => value === text)?.[1]
}

const DOCUMENT = [
  '<?xml version="1.0" encoding="utf-8"?>',
  '<Project xmlns:x="urn:x" Sdk="Microsoft.NET.Sdk">',
  '  <!-- <Compile Include="old.cs" /> is gone -->',
  '  <x:Script><![CDATA[ if (a < b) <tag> ]]></x:Script>',
  '  <None Include="bin\\" />',
  '</Project>',
].join('\n')

test('XML colours element names, attribute names and attribute values', () => {
  expect(classOf(DOCUMENT, 'Project')).toBe('tag')
  expect(classOf(DOCUMENT, 'xmlns:x')).toBe('attr')
  expect(classOf(DOCUMENT, '"urn:x"')).toBe('string')
  expect(classOf(DOCUMENT, 'Sdk')).toBe('attr')
  expect(classOf(DOCUMENT, 'x:Script')).toBe('tag')
  expect(classOf(DOCUMENT, 'None')).toBe('tag')
})

test('the XML declaration is one meta span, not a tag', () => {
  expect(classOf(DOCUMENT, '<?xml version="1.0" encoding="utf-8"?>')).toBe('meta')
})

test('a tag inside an XML comment or a CDATA section is not coloured as markup', () => {
  expect(classOf(DOCUMENT, '<!-- <Compile Include="old.cs" /> is gone -->')).toBe('comment')
  expect(classOf(DOCUMENT, '<![CDATA[ if (a < b) <tag> ]]>')).toBe('string')
  expect(classOf(DOCUMENT, 'Compile')).toBeUndefined()
  expect(classOf(DOCUMENT, 'tag')).toBeUndefined()
})

test('whichever of a comment and a CDATA section opens first keeps the other as its text', () => {
  const cdataFirst = '<a><![CDATA[ x <!-- y ]]></a>\n<b c="d"/>'
  expect(classOf(cdataFirst, '<![CDATA[ x <!-- y ]]>')).toBe('string')
  expect(classOf(cdataFirst, 'b')).toBe('tag')
  const commentFirst = '<!-- old <![CDATA[ --><s><![CDATA[ if (a<b) ]]></s>'
  expect(classOf(commentFirst, '<!-- old <![CDATA[ -->')).toBe('comment')
  expect(classOf(commentFirst, '<![CDATA[ if (a<b) ]]>')).toBe('string')
  expect(classOf(commentFirst, 'b')).toBeUndefined()
})

test('an element or attribute name may use letters outside ASCII', () => {
  const code = '<Größe Wert="1"/><Über ä="2"/>'
  expect(classOf(code, 'Größe')).toBe('tag')
  expect(classOf(code, 'Über')).toBe('tag')
  expect(classOf(code, 'ä')).toBe('attr')
})

test('a DOCTYPE with an internal subset is one meta span', () => {
  const code = '<!DOCTYPE x [ <!ENTITY a "b"> ]>\n<x/>'
  expect(classOf(code, '<!DOCTYPE x [ <!ENTITY a "b"> ]>')).toBe('meta')
  expect(classOf(code, 'x')).toBe('tag')
})

test('a backslash before the closing quote does not escape it, as XML has no escapes there', () => {
  // MSBuild writes folder paths with a trailing backslash. A grammar that reads `\"` as an escape
  // runs the value on into the rest of the file.
  expect(classOf(DOCUMENT, '"bin\\"')).toBe('string')
})

test('a self-closing tag keeps its name a tag and its slash uncoloured', () => {
  expect(classes('<Item Include="a.cs"/>')).toEqual([
    ['<', null],
    ['Item', 'tag'],
    [' ', null],
    ['Include', 'attr'],
    ['=', null],
    ['"a.cs"', 'string'],
    ['/>', null],
  ])
})

test('a `>` inside an attribute value does not end the tag', () => {
  // MSBuild conditions compare versions with a bare `>`, which XML allows inside an attribute.
  const code = `<When Condition="'$(V)' > '1'" Label="x">`
  expect(classOf(code, `"'$(V)' > '1'"`)).toBe('string')
  expect(classOf(code, 'Label')).toBe('attr')
})

test('every XML token maps back to the exact source text', () => {
  expect(
    classes(DOCUMENT)
      .map(([value]) => value)
      .join(''),
  ).toBe(DOCUMENT)
})
