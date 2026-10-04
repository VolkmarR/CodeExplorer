import { describe, expect, it } from 'vite-plus/test'
import { excludedLead, withPattern } from '@/features/projects/excludedPaths'

describe('excludedLead', () => {
  it('says which files were left out as a sentence that runs on into the link', () => {
    // Under a thousand, so the test does not depend on the machine's digit grouping.
    expect(excludedLead(712, 'at HEAD')).toBe('712 files at HEAD are left out by the')
  })

  it('agrees with a single file', () => {
    expect(excludedLead(1, 'at HEAD')).toBe('1 file at HEAD is left out by the')
  })

  it('names the section it was left out of, where two on a page count the same files', () => {
    expect(excludedLead(712, 'at HEAD', 'the language shares')).toBe(
      '712 files at HEAD are left out of the language shares by the',
    )
  })
})

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
