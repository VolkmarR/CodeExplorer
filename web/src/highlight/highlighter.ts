import { createHighlighter } from '@tanstack/highlight'
import { cpp } from '@tanstack/highlight/languages/cpp'
import { css } from '@tanstack/highlight/languages/css'
import { dockerfile } from '@tanstack/highlight/languages/dockerfile'
import { env } from '@tanstack/highlight/languages/env'
import { go } from '@tanstack/highlight/languages/go'
import { html } from '@tanstack/highlight/languages/html'
import { js } from '@tanstack/highlight/languages/js'
import { json } from '@tanstack/highlight/languages/json'
import { jsx } from '@tanstack/highlight/languages/jsx'
import { markdown } from '@tanstack/highlight/languages/markdown'
import { php } from '@tanstack/highlight/languages/php'
import { plaintext } from '@tanstack/highlight/languages/plaintext'
import { python } from '@tanstack/highlight/languages/python'
import { shell } from '@tanstack/highlight/languages/shell'
import { sql } from '@tanstack/highlight/languages/sql'
import { svelte } from '@tanstack/highlight/languages/svelte'
import { toml } from '@tanstack/highlight/languages/toml'
import { ts } from '@tanstack/highlight/languages/ts'
import { tsx } from '@tanstack/highlight/languages/tsx'
import { vue } from '@tanstack/highlight/languages/vue'
import { yaml } from '@tanstack/highlight/languages/yaml'
import { splitFileName } from '@/lib/format'
import { csharp } from './csharp'
import { xsharp } from './xsharp'

/**
 * The grammars the extension map below names, and nothing else. `allLanguages` was the first
 * spelling and pulled every definition the library ships into the file and search chunks — including
 * the ones for Apache and nginx configuration, CMake, diffs, EJS, HTTP, Mermaid, Scheme and TanStack
 * Router's own `tsrx`, none of which the index can ever hand this view, because the map is the only
 * thing that chooses a language and it names none of them.
 *
 * Named one import each rather than filtered out of `allLanguages` at run time, because a filter
 * keeps every definition in the bundle: only an import the bundler can see is absent is absent.
 */
const BUNDLED = [
  cpp,
  css,
  dockerfile,
  env,
  go,
  html,
  js,
  json,
  jsx,
  markdown,
  php,
  plaintext,
  python,
  shell,
  sql,
  svelte,
  toml,
  ts,
  tsx,
  vue,
  yaml,
  csharp,
  xsharp,
] as const

/**
 * Every language the highlighter knows, which is what the map is allowed to name. Typing the map by
 * it is what keeps the two from drifting: an extension pointed at a grammar no longer imported does
 * not silently fall back to plaintext at run time, it fails `tsc`.
 */
type BundledLanguage = (typeof BUNDLED)[number]['name']

/**
 * Created once, at module scope: building it per render would re-compile every pattern on each
 * keystroke in the file view.
 */
export const highlighter = createHighlighter({
  fallbackLanguage: 'plaintext',
  languages: BUNDLED,
})

/**
 * The index has no language column — it stores an extension — so the file view maps one to the
 * other here. An unknown extension falls back to plaintext, which renders as unhighlighted text
 * rather than as nothing.
 */
const LANGUAGE_BY_EXTENSION: Record<string, BundledLanguage> = {
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
