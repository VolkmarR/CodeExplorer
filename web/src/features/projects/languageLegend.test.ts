import { describe, expect, it } from 'vite-plus/test'
import { legendPercent } from '@/features/projects/languageLegend'

describe('legendPercent', () => {
  it('rounds a share to a whole percent', () => {
    expect(legendPercent(0.734)).toBe('73%')
  })

  it('says "<1%" for a share that rounds to nothing, which "0%" would deny is there', () => {
    expect(legendPercent(0.004)).toBe('<1%')
  })

  it('says ">99%" for a share that rounds to all of it while another language has some', () => {
    expect(legendPercent(0.996)).toBe('>99%')
  })

  it('says "100%" for the one language there is', () => {
    expect(legendPercent(1)).toBe('100%')
  })
})
