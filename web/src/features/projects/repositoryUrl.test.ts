import { expect, test } from 'vite-plus/test'
import {
  repositoryUrlShape,
  shortUrl,
  withoutAzureDevOpsUserName,
} from '@/features/projects/repositoryUrl'

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

/**
 * The provider decides which instructions the add-repository form shows, so each host is checked in
 * the shapes its own clone dialog copies: https, scp-style ssh, and the ssh URL form.
 */

const provider = (url: string) => repositoryUrlShape(url)?.provider

test('each known host is recognised from its https and its ssh clone URLs', () => {
  expect(provider('https://dev.azure.com/acsdatasystems/Infominds.Lab/_git/Edilverso')).toBe(
    'azure-devops',
  )
  expect(provider('git@ssh.dev.azure.com:v3/acsdatasystems/Infominds.Lab/Edilverso')).toBe(
    'azure-devops',
  )
  expect(provider('https://acsdatasystems.visualstudio.com/Infominds.Lab/_git/Edilverso')).toBe(
    'azure-devops',
  )
  expect(provider('https://github.com/VolkmarR/CodeExplorer.git')).toBe('github')
  expect(provider('git@github.com:VolkmarR/CodeExplorer.git')).toBe('github')
  expect(provider('https://gitlab.com/acme/platform.git')).toBe('gitlab')
  expect(provider('ssh://git@gitlab.acme.example/acme/platform.git')).toBe('gitlab')
  expect(provider('https://git.acme.example/platform.git')).toBe('other')
})

test('a host is matched whole, not by a name that merely contains it', () => {
  expect(provider('https://github.com.evil.example/x.git')).toBe('other')
  expect(provider('https://notgithub.com/x.git')).toBe('other')
})

test('paths and file URLs are local, a loopback git server is not', () => {
  expect(provider(String.raw`C:\repos\CodeExplorer`)).toBe('local')
  expect(provider('D:/repos/CodeExplorer')).toBe('local')
  expect(provider(String.raw`\\server\share\AcsLib`)).toBe('local')
  expect(provider('file:///C:/repos/CodeExplorer')).toBe('local')
  expect(provider('/srv/git/platform')).toBe('local')
  expect(provider('../platform')).toBe('local')
  // Still a git server over the network, which may want a credential.
  expect(provider('http://localhost:3000/acme/platform.git')).toBe('other')
})

test('ssh is told apart from https, because a token signs in over https only', () => {
  expect(repositoryUrlShape('git@github.com:VolkmarR/CodeExplorer.git')?.ssh).toBe(true)
  expect(repositoryUrlShape('ssh://git@github.com/VolkmarR/CodeExplorer.git')?.ssh).toBe(true)
  expect(repositoryUrlShape('https://github.com/VolkmarR/CodeExplorer.git')?.ssh).toBe(false)
})

test('the user name Azure DevOps puts in its clone URL is found and can be removed', () => {
  const shape = repositoryUrlShape(
    'https://acsdatasystems@dev.azure.com/acsdatasystems/Infominds.Lab/_git/Edilverso',
  )
  expect(shape?.provider).toBe('azure-devops')
  expect(shape?.userName).toBe('acsdatasystems')
  expect(shape?.secret).toBeNull()
  expect(shape?.withoutUserPart).toBe(
    'https://dev.azure.com/acsdatasystems/Infominds.Lab/_git/Edilverso',
  )
})

test('a token in the URL is taken out of it, to be moved to the credential field', () => {
  const shape = repositoryUrlShape('https://user:s3cr%40t@github.com/acme/platform.git')
  expect(shape?.userName).toBe('user')
  expect(shape?.secret).toBe('s3cr@t')
  expect(shape?.withoutUserPart).toBe('https://github.com/acme/platform.git')
})

test('the Azure DevOps user name is removed as pasted, and nothing else is', () => {
  expect(
    withoutAzureDevOpsUserName(
      'https://acsdatasystems@dev.azure.com/acsdatasystems/Infominds.Lab/_git/Edilverso',
    ),
  ).toEqual({
    url: 'https://dev.azure.com/acsdatasystems/Infominds.Lab/_git/Edilverso',
    userName: 'acsdatasystems',
  })
  // Already clean: nothing to say was removed.
  expect(
    withoutAzureDevOpsUserName('https://dev.azure.com/acsdatasystems/Infominds.Lab/_git/Edilverso'),
  ).toBeNull()
  // A token is a secret, moved to the credential field on request rather than dropped unseen.
  expect(withoutAzureDevOpsUserName('https://org:pat@dev.azure.com/org/p/_git/r')).toBeNull()
  // Another host's user name is left for the fix button, where it is seen being removed.
  expect(withoutAzureDevOpsUserName('https://user@github.com/acme/platform.git')).toBeNull()
})

test('a URL without a user part has nothing to remove', () => {
  const shape = repositoryUrlShape('https://github.com/acme/platform.git')
  expect(shape?.userName).toBeNull()
  expect(shape?.withoutUserPart).toBeNull()
})

test('an empty field has no shape, and nonsense is an unknown host rather than an error', () => {
  expect(repositoryUrlShape('')).toBeNull()
  expect(repositoryUrlShape('   ')).toBeNull()
  expect(provider('not a url')).toBe('other')
})
