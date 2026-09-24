import { describe, expect, it } from 'vite-plus/test'
import { isNewlyFinished } from './useRefreshWatcher'

const P = 'acslib'

describe('isNewlyFinished', () => {
  it('says nothing happened when the first answer of a page load is a long-finished refresh', () => {
    // The cache was filled from exactly this index, so dropping it would refetch what it just got.
    expect(
      isNewlyFinished(
        { finishedAt: undefined, project: P },
        { finishedAt: 'T1', project: P },
        'Succeeded',
      ),
    ).toBe(false)
  })

  it('invalidates when a refresh this tab watched finishes', () => {
    expect(
      isNewlyFinished(
        { finishedAt: null, project: P },
        { finishedAt: 'T2', project: P },
        'Succeeded',
      ),
    ).toBe(true)
  })

  it('invalidates when a refresh started elsewhere is only found afterwards', () => {
    // The status polls only while one is running, so a refresh from the MCP tool or a second tab
    // begins and ends unobserved: the state never leaves `Succeeded` and only the identity moves.
    expect(
      isNewlyFinished(
        { finishedAt: 'T1', project: P },
        { finishedAt: 'T2', project: P },
        'Succeeded',
      ),
    ).toBe(true)
  })

  it('stays quiet while the same finished refresh is reported again', () => {
    // This is the original bug: `Succeeded` is reported for days, and every mount read it as news.
    expect(
      isNewlyFinished(
        { finishedAt: 'T1', project: P },
        { finishedAt: 'T1', project: P },
        'Succeeded',
      ),
    ).toBe(false)
  })

  it('stays quiet while a refresh is still running', () => {
    expect(
      isNewlyFinished(
        { finishedAt: 'T1', project: P },
        { finishedAt: null, project: P },
        'Running',
      ),
    ).toBe(false)
    expect(
      isNewlyFinished({ finishedAt: 'T1', project: P }, { finishedAt: null, project: P }, 'Queued'),
    ).toBe(false)
  })

  it('leaves the index alone when a refresh fails, and drops it when the next one succeeds', () => {
    expect(
      isNewlyFinished({ finishedAt: 'T1', project: P }, { finishedAt: 'T2', project: P }, 'Failed'),
    ).toBe(false)
    expect(
      isNewlyFinished(
        { finishedAt: 'T2', project: P },
        { finishedAt: 'T3', project: P },
        'Succeeded',
      ),
    ).toBe(true)
  })

  it('does not treat changing project as a transition', () => {
    expect(
      isNewlyFinished(
        { finishedAt: 'T1', project: 'other' },
        { finishedAt: 'T9', project: P },
        'Succeeded',
      ),
    ).toBe(false)
  })

  it('does nothing when no project is open', () => {
    expect(
      isNewlyFinished(
        { finishedAt: 'T1', project: undefined },
        { finishedAt: 'T2', project: undefined },
        'Succeeded',
      ),
    ).toBe(false)
  })

  it('does nothing for a project that has never finished a refresh', () => {
    expect(
      isNewlyFinished(
        { finishedAt: null, project: P },
        { finishedAt: null, project: P },
        'Succeeded',
      ),
    ).toBe(false)
  })
})
