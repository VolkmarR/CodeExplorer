import { isValidElement, type ReactElement, type ReactNode } from 'react'
import { describe, expect, it } from 'vite-plus/test'
import { PageHeader } from '@/components/PageHeader'

/**
 * The header is called as a plain function, as `OverviewCard.test.tsx` does: it holds no state and
 * no hooks, so its element tree is the whole answer. The tree is flattened into the text it shows
 * and the elements it places.
 */
function flatten(node: ReactNode): { text: string; elements: ReactElement[] } {
  const elements: ReactElement[] = []
  const text: string[] = []
  const walk = (n: ReactNode): void => {
    if (typeof n === 'string' || typeof n === 'number') text.push(String(n))
    else if (Array.isArray(n)) n.forEach(walk)
    else if (isValidElement(n)) {
      elements.push(n)
      walk((n.props as { children?: ReactNode }).children)
    }
  }
  walk(node)
  return { elements, text: text.join(' ') }
}

const headings = (elements: ReactElement[]) => elements.filter((e) => e.type === 'h1')

describe('PageHeader', () => {
  it('draws the title as the one h1, at the page heading size', () => {
    const { elements } = flatten(PageHeader({ title: 'Projects' }))
    const [h1, ...rest] = headings(elements)
    expect(rest).toEqual([])
    expect((h1.props as { className: string }).className.split(' ')).toContain('text-lg')
  })

  it('draws the hint and the actions after the title', () => {
    const { text } = flatten(
      PageHeader({ title: 'Projects', hint: 'what a search spans', actions: 'New project' }),
    )
    expect(text).toBe('Projects what a search spans New project')
  })

  it('draws only the title where there is no hint and no actions', () => {
    expect(flatten(PageHeader({ title: 'Not found' })).text).toBe('Not found')
  })
})
