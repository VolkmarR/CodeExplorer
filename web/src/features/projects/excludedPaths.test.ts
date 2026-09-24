import { describe, expect, it } from 'vite-plus/test'
import { withPattern } from '@/features/projects/excludedPaths'

describe('withPattern', () => {
  it('adds the pattern on its own line after what the operator wrote', () => {
    expect(withPattern('**/*.rc', '**/*.min.js')).toBe('**/*.rc\n**/*.min.js')
  })

  it('fills an empty draft without a leading blank line', () => {
    expect(withPattern('', '**/*.min.js')).toBe('**/*.min.js')
  })

  it('does not stack blank lines left at the end of the draft', () => {
    expect(withPattern('**/*.rc\n\n', '**/*.min.js')).toBe('**/*.rc\n**/*.min.js')
  })

  it('leaves the draft alone where it already holds the pattern in any case', () => {
    expect(withPattern('**/*.MIN.js\n', '**/*.min.js')).toBe('**/*.MIN.js\n')
  })
})
