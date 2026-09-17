import { expect, test } from 'vite-plus/test'
import { mcpEndpoint } from '@/features/projects/endpoints'

/**
 * The one string an operator opens this app to copy, so it is worth a test: an endpoint that is
 * almost right is worse than none, because it fails inside an agent's configuration rather than here.
 */

test('the endpoint is the project group with /mcp under it, on this origin', () => {
  expect(mcpEndpoint('https://code.infominds.eu', 'acslib')).toBe(
    'https://code.infominds.eu/projects/acslib/mcp',
  )
})

test('a development origin keeps its port', () => {
  expect(mcpEndpoint('http://localhost:5173', 'acslib')).toBe(
    'http://localhost:5173/projects/acslib/mcp',
  )
})

test('a trailing slash on the origin does not double up', () => {
  expect(mcpEndpoint('https://code.infominds.eu/', 'acslib')).toBe(
    'https://code.infominds.eu/projects/acslib/mcp',
  )
})
