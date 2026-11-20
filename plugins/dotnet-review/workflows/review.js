export const meta = {
  name: 'review',
  description: 'Reviews a .NET / EF Core branch against its base: one agent sorts the changed files, one reviewer per touched area (migrations, EF queries, API contract) comments in parallel, a verifier drops every comment its evidence does not support, and at most five are written to review-<branch>.md. Nothing is posted. args: base (the git ref compared with, e.g. main), branch (used in the file name), outDir (absolute folder for the file); optional dialect (PostgreSql | SqlServer) when the dblens plugin is connected to the database the code targets, efScript (true lets the migrations reviewer run `dotnet ef migrations script` for the new migrations: it builds the project, so ask the user first)',
  whenToUse: 'The user asks for a review of a .NET or EF Core branch or pull request',
  phases: [{ title: 'Classify' }, { title: 'Review' }, { title: 'Verify' }, { title: 'Write' }],
}

// args: { base, branch, outDir, dialect?, efScript? }

const AREAS = ['migrations', 'ef-queries', 'api-contract']

const FILES = {
  type: 'object',
  required: ['files'],
  properties: {
    files: {
      type: 'array',
      items: {
        type: 'object',
        required: ['path', 'category'],
        properties: { path: { type: 'string' }, category: { enum: [...AREAS, 'other'] } },
      },
    },
  },
}

const COMMENTS = {
  type: 'object',
  required: ['category', 'comments', 'notCovered'],
  properties: {
    category: { type: 'string' },
    comments: {
      type: 'array',
      items: {
        type: 'object',
        required: ['id', 'file', 'line', 'severity', 'title', 'body', 'evidenceKind', 'evidence'],
        properties: {
          id: { type: 'string' },
          file: { type: 'string' },
          line: { type: 'integer' },
          severity: { enum: ['High', 'Medium', 'Low'] },
          title: { type: 'string' },
          body: { type: 'string' },
          evidenceKind: { enum: ['snippet', 'tool'] },
          evidence: { type: 'string' },
        },
      },
    },
    notCovered: { type: 'array', items: { type: 'string' } },
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
        required: ['id', 'keep', 'reason'],
        properties: { id: { type: 'string' }, keep: { type: 'boolean' }, reason: { type: 'string' } },
      },
    },
  },
}

const WRITTEN = {
  type: 'object',
  required: ['path', 'summary'],
  properties: { path: { type: 'string' }, summary: { type: 'string' } },
}

const a = args || {}
const KNOWN = ['base', 'branch', 'outDir', 'dialect', 'efScript']
const unknown = Object.keys(a).filter(k => !KNOWN.includes(k))
if (unknown.length) throw new Error(`Unknown args ${unknown.join(', ')}; accepted: ${KNOWN.join(', ')}`)
if (!a.base || !a.branch || !a.outDir) throw new Error('args needs base, branch and outDir')
const range = `${a.base}...HEAD`
const database = a.dialect
  ? `The dblens tools are connected to the database this code targets (dialect ${a.dialect}).`
  : 'No database: do not call the dblens tools.'

phase('Classify')
const sorted = await agent(`Sort the files changed in ${range}.`, { agentType: 'dotnet-review:classify', schema: FILES, label: 'classify' })
if (!sorted) throw new Error('The classify agent returned nothing.')

const touched = AREAS
  .map(area => ({ area, files: sorted.files.filter(f => f.category === area).map(f => f.path) }))
  .filter(t => t.files.length > 0)

const SEVERITY = { High: 0, Medium: 1, Low: 2 }
const groups = await pipeline(touched,
  t => agent(
    `Review ${range}, area ${t.area}. Files: ${t.files.join(', ')}. ${database} ` +
    (t.area === 'migrations'
      ? (a.efScript === true ? `Building the project to script the new migrations is allowed; write scripts into ${a.outDir}. ` : 'Building the project is not allowed. ')
      : '') +
    `Set "category" to "${t.area}".`,
    { agentType: `dotnet-review:${t.area}`, schema: COMMENTS, phase: 'Review', label: t.area }),
  (result, t) => {
    if (!result) return { category: t.area, comments: [], notCovered: [`The ${t.area} reviewer returned nothing.`], kept: [] }
    if (result.comments.length === 0) return { ...result, kept: [] }
    return agent(
      `Check these ${t.area} comments on ${range}.\n${JSON.stringify(result.comments)}`,
      { agentType: 'dotnet-review:verifier', schema: VERDICTS, phase: 'Verify', label: `verify ${t.area}` })
      .then(v => {
        // A comment the verifier did not keep, or did not answer for, is dropped: evidence first.
        const keep = new Set((v ? v.verdicts : []).filter(x => x.keep).map(x => x.id))
        const kept = result.comments.filter(c => keep.has(c.id))
        return { ...result, kept, dropped: result.comments.length - kept.length }
      })
  })

phase('Write')
const done = groups.filter(Boolean)
// Two reviewers commenting on one line saw the same problem: the more severe comment stays. One
// reviewer's comments on a line are distinct problems.
const kept = []
for (const c of done.flatMap(g => g.kept.map(k => ({ ...k, area: g.category })))) {
  const twin = kept.findIndex(k => k.file === c.file && k.line === c.line && k.area !== c.area)
  if (twin < 0) kept.push(c)
  else if (SEVERITY[c.severity] < SEVERITY[kept[twin].severity]) kept[twin] = c
}
const ranked = kept.sort((x, y) =>
  SEVERITY[x.severity] - SEVERITY[y.severity] || x.file.localeCompare(y.file) || x.line - y.line)
const review = {
  branch: a.branch,
  base: a.base,
  areas: touched.map(t => t.area),
  comments: ranked.slice(0, 5),
  beyondFive: Math.max(0, ranked.length - 5),
  dropped: done.reduce((n, g) => n + (g.dropped || 0), 0),
  notCovered: done.flatMap(g => g.notCovered.map(s => `${g.category}: ${s}`)),
}
return await agent(
  `Write the review. Output folder: ${a.outDir}.\n${JSON.stringify(review)}`,
  { agentType: 'dotnet-review:writer', schema: WRITTEN, label: 'write' })
