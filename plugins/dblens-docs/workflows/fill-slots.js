export const meta = {
  name: 'fill-slots',
  description: 'Writes the words of the empty or stale dblens-docs slots: one writer agent per batch of slots, in parallel, a reviewer that reads each written slot against the source, and one rewrite of what the code does not support. args: batches (an array of arrays of item ids, as get_doc_slots lists them; a batch holds the slots of one document, at most 8)',
  whenToUse: 'The project documents were scaffolded by dblens-docs and their slots need words',
  phases: [{ title: 'Write' }, { title: 'Review' }, { title: 'Rewrite' }],
}

// args: { batches: string[][] }

const WRITTEN = {
  type: 'object',
  required: ['written', 'skipped'],
  properties: {
    written: {
      type: 'array',
      items: { type: 'object', required: ['id', 'text'], properties: { id: { type: 'string' }, text: { type: 'string' } } },
    },
    skipped: {
      type: 'array',
      items: { type: 'object', required: ['id', 'reason'], properties: { id: { type: 'string' }, reason: { type: 'string' } } },
    },
  },
}

const VERDICTS = {
  type: 'object',
  required: ['verdicts'],
  properties: {
    verdicts: {
      type: 'array',
      items: {
        type: 'object',
        required: ['id', 'supported', 'problem'],
        properties: { id: { type: 'string' }, supported: { type: 'boolean' }, problem: { type: 'string' } },
      },
    },
  },
}

const a = args || {}
const unknown = Object.keys(a).filter(k => k !== 'batches')
if (unknown.length) throw new Error(`Unknown args ${unknown.join(', ')}; accepted: batches`)
if (!Array.isArray(a.batches) || a.batches.length === 0 || a.batches.some(b => !Array.isArray(b) || b.length === 0 || b.length > 8))
  throw new Error('args.batches is an array of non-empty arrays of at most 8 item ids')

const results = await pipeline(a.batches,
  ids => agent(`Write the slots of these items: ${JSON.stringify(ids)}.`,
    { agentType: 'dblens-docs:writer', schema: WRITTEN, phase: 'Write', label: `write ${ids.length}` }),
  async (written, ids) => {
    if (!written) return { written: [], skipped: ids.map(id => ({ id, reason: 'the writer returned nothing' })), unsupported: [] }
    const wrote = written.written.map(w => w.id)
    if (wrote.length === 0) return { ...written, unsupported: [] }

    const review = await agent(`Check the words of these slots against the code: ${JSON.stringify(wrote)}.`,
      { agentType: 'dblens-docs:reviewer', schema: VERDICTS, phase: 'Review', label: `review ${wrote.length}` })
    // A slot the reviewer did not answer for is not supported: words are the one thing that must not be a guess.
    const bad = wrote
      .map(id => ({ id, v: review ? review.verdicts.find(x => x.id === id) : null }))
      .filter(x => !x.v || !x.v.supported)
      .map(x => ({ id: x.id, problem: x.v ? x.v.problem : 'the reviewer gave no verdict' }))
    if (bad.length === 0) return { ...written, unsupported: [] }

    const again = await agent(
      `Rewrite these slots. The reviewer did not find their words in the code: ${JSON.stringify(bad)}. Say only what the code shows; if you cannot, skip the id and say why.`,
      { agentType: 'dblens-docs:writer', schema: WRITTEN, phase: 'Rewrite', label: `rewrite ${bad.length}` })
    const redone = new Set(again ? again.written.map(w => w.id) : [])
    return {
      written: written.written,
      skipped: [...written.skipped, ...(again ? again.skipped : [])],
      // Rewritten once, not reviewed again: the user is told.
      unsupported: bad.map(b => ({ ...b, rewritten: redone.has(b.id) })),
    }
  })

const done = results.filter(Boolean)
return {
  written: done.flatMap(r => r.written.map(w => w.id)),
  skipped: done.flatMap(r => r.skipped),
  rewritten: done.flatMap(r => r.unsupported),
  batchesFailed: results.length - done.length,
}
