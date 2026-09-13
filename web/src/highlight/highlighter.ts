import { allLanguages, createHighlighter } from '@tanstack/highlight'
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

export function languageFor(qualifiedPath: string): string {
  const name = qualifiedPath.slice(qualifiedPath.lastIndexOf('/') + 1)
  const dot = name.lastIndexOf('.')
  // A dotfile such as `.gitignore` has no extension; so does `Dockerfile`, which the whole name names.
  const key = dot > 0 ? name.slice(dot + 1).toLowerCase() : name.toLowerCase()
  return LANGUAGE_BY_EXTENSION[key] ?? 'plaintext'
}
