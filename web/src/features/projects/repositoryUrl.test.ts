import { expect, test } from 'vite-plus/test'
import { shortUrl } from '@/features/projects/repositoryUrl'

/**
 * Shortening is only ever right when it is reversible in a reader's head: the point is to tell two
 * repositories apart, so a form this cannot parse must show as written rather than as a plausible
 * wrong answer. The three forms below are the ones an operator actually pastes.
 */

test('a https remote shows its host and its last segment', () => {
  expect(shortUrl('https://github.com/VolkmarR/CodeExplorer.git')).toBe(
    'github.com/…/CodeExplorer.git',
  )
  expect(shortUrl('https://dev.azure.com/infominds/Radix/_git/AcsLib')).toBe(
    'dev.azure.com/…/AcsLib',
  )
  // A trailing slash is not a segment: the name is still the name.
  expect(shortUrl('https://github.com/VolkmarR/CodeExplorer/')).toBe('github.com/…/CodeExplorer')
})

test('an scp-style remote shows as written, because it is not a URL the browser parses', () => {
  expect(shortUrl('git@github.com:VolkmarR/CodeExplorer.git')).toBe(
    'git@github.com:VolkmarR/CodeExplorer.git',
  )
})

test('a Windows path shows as written, not as a scheme with no host', () => {
  // `C:\repos\x` parses as the scheme `c:`, so without the host check it would shorten to `/…/x`
  // and hide which drive and folder it came from — the only thing that tells two local clones apart.
  expect(shortUrl(String.raw`C:\repos\CodeExplorer`)).toBe(String.raw`C:\repos\CodeExplorer`)
  expect(shortUrl(String.raw`D:\work\AcsLib\.git`)).toBe(String.raw`D:\work\AcsLib\.git`)
})

test('a file URL with a host of its own is still shortened', () => {
  expect(shortUrl('file://server/share/AcsLib')).toBe('server/…/AcsLib')
})

test('a port is part of the host, because it is part of which server this is', () => {
  expect(shortUrl('https://git.local:8443/team/AcsLib.git')).toBe('git.local:8443/…/AcsLib.git')
})

test('an ssh URL is shortened like any other the browser parses', () => {
  expect(shortUrl('ssh://git@github.com/VolkmarR/CodeExplorer.git')).toBe(
    'github.com/…/CodeExplorer.git',
  )
})

test('a host with no path keeps the ellipsis rather than losing the shape', () => {
  expect(shortUrl('https://github.com')).toBe('github.com/…/')
})

test('nonsense shows as written rather than throwing', () => {
  expect(shortUrl('')).toBe('')
  expect(shortUrl('not a url')).toBe('not a url')
})
