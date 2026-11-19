export const meta = {
  name: 'audit',
  description: 'Audits a PostgreSQL or SQL Server database with dblens: agents run the diagnostics in parallel, a verifier re-runs every finding, and a report keeps the evidence',
  whenToUse: 'The user asks for a dblens audit, health check or review of a database',
  phases: [{ title: 'Orient' }, { title: 'Analyze' }, { title: 'Verify' }, { title: 'Report' }],
}

// args: { dialect: 'PostgreSql' | 'SqlServer', schema?, outDir, runLabel, queriesFile?, efSnapshot?, privacy? }
// runLabel and outDir come from the caller: a workflow has no clock and no filesystem.

const FINDINGS = {
  type: 'object',
  required: ['agent', 'findings', 'notCovered'],
  properties: {
    agent: { type: 'string' },
    findings: {
      type: 'array',
      items: {
        type: 'object',
        required: ['id', 'sourceTool', 'toolParams', 'ruleId', 'severity', 'evidence', 'table', 'title'],
        properties: {
          id: { type: 'string' },
          sourceTool: { type: 'string' },
          toolParams: { type: 'object' },
          ruleId: { type: 'string' },
          severity: { enum: ['Critical', 'High', 'Medium', 'Low', 'Info'] },
          evidence: { enum: ['Measured', 'Derived', 'Inferred'] },
          table: { type: 'string' },
          columns: { type: 'array', items: { type: 'string' } },
          title: { type: 'string' },
          explanation: { type: 'string' },
          sqlFix: { type: ['string', 'null'] },
        },
      },
    },
    notCovered: { type: 'array', items: { type: 'string' } },
  },
}

const VERDICTS = {
  type: 'object',
  required: ['agent', 'verdicts'],
  properties: {
    agent: { type: 'string' },
    verdicts: {
      type: 'array',
      items: {
        type: 'object',
        required: ['id', 'verdict', 'reason'],
        properties: {
          id: { type: 'string' },
          verdict: { enum: ['confirmed', 'downgraded', 'rejected', 'unverified'] },
          reason: { type: 'string' },
        },
      },
    },
  },
}

const SCOPE = {
  type: 'object',
  required: ['dialect', 'database', 'schemas', 'tableCount'],
  properties: {
    dialect: { type: 'string' },
    database: { type: 'string' },
    schemas: { type: 'array', items: { type: 'string' } },
    tableCount: { type: 'integer' },
    error: { type: 'string' },
  },
}

const REPORT = {
  type: 'object',
  required: ['reportPath', 'summary'],
  properties: { reportPath: { type: 'string' }, summary: { type: 'string' } },
}

const a = args || {}
if (!a.dialect || !a.outDir || !a.runLabel) throw new Error('args needs dialect, outDir and runLabel')
const target = a.schema ? `dialect ${a.dialect}, schema ${a.schema}` : `dialect ${a.dialect}, every user schema`

phase('Orient')
const scope = await agent(`Scope of the audit: ${target}.`, { agentType: 'dblens:orient', schema: SCOPE, label: 'orient' })
if (!scope || scope.error) throw new Error(`dblens could not read the database: ${scope ? scope.error : 'no answer'}`)

const analysts = [
  { name: 'indexes', task: '' },
  { name: 'integrity', task: '' },
  { name: 'hygiene', task: a.efSnapshot ? `EF snapshot path: ${a.efSnapshot}.` : '' },
  { name: 'workload', task: a.queriesFile ? `Queries file: ${a.queriesFile}.` : '' },
]
if (a.efSnapshot) analysts.push({ name: 'ef', task: `EF snapshot path: ${a.efSnapshot}.` })
if (a.privacy) analysts.push({ name: 'privacy', task: '' })

const groups = await pipeline(analysts,
  analyst => agent(
    `Audit scope: ${target} (database ${scope.database}, ${scope.tableCount} tables). ${analyst.task} ` +
    `Set "agent" to "${analyst.name}".`,
    { agentType: `dblens:${analyst.name}`, schema: FINDINGS, phase: 'Analyze', label: analyst.name }),
  (result, analyst) => {
    if (!result) return { agent: analyst.name, findings: [], notCovered: [`The ${analyst.name} agent returned nothing.`], verdicts: [] }
    if (result.findings.length === 0) return { ...result, verdicts: [] }
    return agent(
      `Verify these findings of the ${analyst.name} agent (${target}). Set "agent" to "${analyst.name}".\n` +
      JSON.stringify(result.findings),
      { agentType: 'dblens:verifier', schema: VERDICTS, phase: 'Verify', label: `verify ${analyst.name}` })
      .then(v => ({ ...result, verdicts: v ? v.verdicts : result.findings.map(f => (
        { id: f.id, verdict: 'unverified', reason: 'The verifier returned nothing.' })) }))
  })

phase('Report')
const audit = { runLabel: a.runLabel, scope, groups: groups.filter(Boolean) }
return await agent(
  `Write the report. Output folder: ${a.outDir}. runLabel: ${a.runLabel}.\n${JSON.stringify(audit)}`,
  { agentType: 'dblens:reporter', schema: REPORT, label: 'report' })
