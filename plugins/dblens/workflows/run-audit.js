export const meta = {
  name: 'run-audit',
  description: 'The audit proper, started by the /dblens:audit skill after the user has chosen and confirmed the database: agents run the diagnostics in parallel, a verifier re-runs every finding, and a report keeps the evidence. args: dialect (PostgreSql | SqlServer), expectedDatabase (the database the user confirmed: the run stops if the server reaches another), outDir (absolute folder for the report), runLabel (e.g. the date); optional connection (the name of a saved connection; omitted = the database the server is configured with), schema, efSnapshot (absolute path of an EF Core *ModelSnapshot.cs or its folder), efScript (true lets the ef agent run the `dotnet ef migrations script` command for the pending migrations and check the script: it builds the user\'s project, so ask the user first), queriesFile (a file of SQL queries to plan), privacy (true to scan column names for personal data)',
  whenToUse: 'The user asks for a dblens audit, health check or review of a database',
  phases: [{ title: 'Orient' }, { title: 'Analyze' }, { title: 'Verify' }, { title: 'Report' }],
}

// args: { dialect: 'PostgreSql' | 'SqlServer', expectedDatabase, connection?, schema?, outDir, runLabel, queriesFile?, efSnapshot?, efScript?, privacy? }
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
          sqlFixKind: { enum: ['Ddl', 'ReadOnlyProbe', null] },
          verification: { enum: ['Confirmed', 'Refuted', 'Inconclusive', 'NotAttempted', 'Failed', null] },
          queryId: { type: ['string', 'null'] },
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
          verdict: { enum: ['confirmed', 'cleared', 'downgraded', 'rejected', 'unverified'] },
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
    target: {
      type: 'object',
      properties: {
        dialect: { type: 'string' }, host: { type: ['string', 'null'] }, port: { type: ['integer', 'null'] },
        database: { type: ['string', 'null'] }, user: { type: ['string', 'null'] }, isLocal: { type: 'boolean' },
        source: { type: 'string' }, connection: { type: ['string', 'null'] },
      },
    },
    serverVersion: { type: 'string' },
    dblensVersion: { type: 'string' },
    error: { type: 'string' },
  },
}

const REPORT = {
  type: 'object',
  required: ['reportPath', 'summary'],
  properties: { reportPath: { type: 'string' }, summary: { type: 'string' } },
}

const a = args || {}
const KNOWN = ['dialect', 'expectedDatabase', 'connection', 'schema', 'outDir', 'runLabel', 'queriesFile', 'efSnapshot', 'efScript', 'privacy']
// A misspelt option would silently skip an analyst: refuse it instead.
const unknown = Object.keys(a).filter(k => !KNOWN.includes(k))
if (unknown.length) throw new Error(`Unknown args ${unknown.join(', ')}; accepted: ${KNOWN.join(', ')}`)
if (!a.dialect || !a.outDir || !a.runLabel) throw new Error('args needs dialect, outDir and runLabel')
// The audit never picks its own database: whoever starts it names the one it must reach.
if (!a.expectedDatabase) throw new Error('args needs expectedDatabase: start the audit with /dblens:audit, which asks which database and confirms it')
// A name only: a password never goes into an agent's prompt.
if (a.connection !== undefined && !/^[A-Za-z0-9_.-]+$/.test(a.connection)) throw new Error('connection is the name of a saved connection, not a connection string')
const database = a.connection ? `Pass connection="${a.connection}" (and dialect ${a.dialect}) to every dblens tool call.` : `Pass dialect ${a.dialect} to every dblens tool call, and no connection: the server's configured database is the one audited.`
const target = a.schema ? `dialect ${a.dialect}, schema ${a.schema}` : `dialect ${a.dialect}, every user schema`

phase('Orient')
// The plugin's server may still be starting on the first call: up to three attempts.
let scope = null
for (let attempt = 1; attempt <= 3 && (!scope || scope.error); attempt++)
  scope = await agent(`Scope of the audit: ${target}. ${database}`, { agentType: 'dblens:orient', schema: SCOPE, label: `orient ${attempt}` })
if (!scope || scope.error) throw new Error(`dblens could not read the database: ${scope ? scope.error : 'no answer'}`)
// Wrong database: stop before any analyst touches it.
if (scope.database !== a.expectedDatabase)
  throw new Error(`The server reached database "${scope.database}"${scope.target ? ` on ${scope.target.host}:${scope.target.port} (${scope.target.source})` : ''}, not the expected "${a.expectedDatabase}": nothing was audited.`)
// An empty scope would read as a clean database: a mistyped schema is an error, not an all-clear.
if (scope.tableCount === 0) throw new Error(`No tables were found for ${target}: check the schema name, nothing was audited.`)

const analysts = [
  { name: 'indexes', task: '' },
  { name: 'integrity', task: '' },
  { name: 'hygiene', task: a.efSnapshot ? `EF snapshot path: ${a.efSnapshot}.` : '' },
  { name: 'workload', task: a.queriesFile ? `Queries file: ${a.queriesFile}.` : '' },
]
if (a.efSnapshot) analysts.push({ name: 'ef', task: `EF snapshot path: ${a.efSnapshot}. Output folder: ${a.outDir}. ` +
  (a.efScript === true ? 'Building the project to script the pending migrations is allowed.' : 'Building the project is not allowed.') })
if (a.privacy) analysts.push({ name: 'privacy', task: '' })

const groups = await pipeline(analysts,
  analyst => agent(
    `Audit scope: ${target} (database ${scope.database}, ${scope.tableCount} tables). ${database} ${analyst.task} ` +
    `Set "agent" to "${analyst.name}".`,
    { agentType: `dblens:${analyst.name}`, schema: FINDINGS, phase: 'Analyze', label: analyst.name }),
  (result, analyst) => {
    if (!result) return { agent: analyst.name, failed: true, findings: [], notCovered: [`The ${analyst.name} agent returned nothing.`], verdicts: [] }
    if (result.findings.length === 0) return { ...result, verdicts: [] }
    return agent(
      `Verify these findings of the ${analyst.name} agent (${target}). ${database} Set "agent" to "${analyst.name}".\n` +
      JSON.stringify(result.findings),
      { agentType: 'dblens:verifier', schema: VERDICTS, phase: 'Verify', label: `verify ${analyst.name}` })
      .then(v => {
        // Every finding gets exactly one verdict: one the verifier skipped is unverified, never lost.
        const given = new Map((v ? v.verdicts : []).map(x => [x.id, x]))
        return { ...result, verdicts: result.findings.map(f => given.get(f.id) ||
          { id: f.id, verdict: 'unverified', reason: 'The verifier returned no verdict for it.' }) }
      })
  })

phase('Report')
const done = groups.filter(Boolean)
const audit = { runLabel: a.runLabel, scope, failedAgents: done.filter(g => g.failed).map(g => g.agent), groups: done }
return await agent(
  `Write the report. Output folder: ${a.outDir}. runLabel: ${a.runLabel}.\n${JSON.stringify(audit)}`,
  { agentType: 'dblens:reporter', schema: REPORT, label: 'report' })
