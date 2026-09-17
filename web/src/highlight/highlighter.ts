import { allLanguages, createHighlighter } from '@tanstack/highlight'
import { splitFileName } from '@/lib/format'
import { csharp } from './csharp'
import { xsharp } from './xsharp'

/**
 * Every language the library ships plus the two definitions this repo owns. Created once, at module
 * scope: building it per render would re-compile every pattern on each keystroke in the file view.
 */
export const highlighter = createHighlighter({
  fallbackLanguage: 'plaintext',
  languages: [...allLanguages, csharp, xsharp],
})

/**
 * The index has no language column — it stores an extension — so the file view maps one to the
 * other here. An unknown extension falls back to plaintext, which renders as unhighlighted text
 * rather than as nothing.
 */
const LANGUAGE_BY_EXTENSION: Record<string, string> = {
  bash: 'shell',
  c: 'cpp',
  cc: 'cpp',
  cjs: 'js',
  cpp: 'cpp',
  cs: 'csharp',
  csx: 'csharp',
  css: 'css',
  dockerfile: 'dockerfile',
  env: 'env',
  go: 'go',
  h: 'cpp',
  hpp: 'cpp',
  htm: 'html',
  html: 'html',
  js: 'js',
  json: 'json',
  jsonc: 'json',
  jsx: 'jsx',
  md: 'markdown',
  mjs: 'js',
  php: 'php',
  ppo: 'xsharp',
  prg: 'xsharp',
  ps1: 'shell',
  py: 'python',
  sh: 'shell',
  sql: 'sql',
  svelte: 'svelte',
  toml: 'toml',
  ts: 'ts',
  tsx: 'tsx',
  vh: 'xsharp',
  vue: 'vue',
  xh: 'xsharp',
  yaml: 'yaml',
  yml: 'yaml',
}

/**
 * Which language definition to tokenize a file with, from its name. The extension rule itself is
 * `lib/format.ts`'s, shared with the file rail so the two cannot disagree about what an extension is.
 */
export function languageFor(qualifiedPath: string): string {
  // A file with no extension is looked up by its whole name, which is what `Dockerfile` and
  // `.gitignore` need. `splitFileName` is where the rule for which is which lives.
  const { extension, stem } = splitFileName(qualifiedPath)
  return LANGUAGE_BY_EXTENSION[(extension || stem).toLowerCase()] ?? 'plaintext'
}
