import assert from 'node:assert/strict';
import { spawn, execFile as execFileCallback } from 'node:child_process';
import { promisify } from 'node:util';
import { createServer } from 'node:http';
import { mkdtemp, mkdir, writeFile, readFile, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { resolve, join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import { chromium } from 'playwright';
const execFile = promisify(execFileCallback), repo = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const root = await mkdtemp(join(tmpdir(), 'aicos-company-e2e-')), data = join(root, 'data'), source = join(root, 'repo');
const output = process.env.AICOS_QA_OUTPUT_DIR || await mkdtemp(join(tmpdir(), 'aicos-company-qa-'));
const base = 'http://127.0.0.1:4173', cp = '/api/v1/companies/software-studio', model = 'company-fixture:latest';
const modelPayloads = [], errors = [], outbound = []; let server, browser, page, checks = 0, runId, modelMode = 'ok';
function check(value, message) { assert.ok(value, message); checks++; }
const modelServer = createServer(async (req, res) => {
  res.setHeader('Content-Type', 'application/json');
  if (req.url === '/api/tags') return res.end(JSON.stringify({ models: [{ name: model, digest: 'a'.repeat(64), details: { format: 'gguf', parameter_size: 'protocol fixture' } }] }));
  if (req.url === '/api/show') return res.end(JSON.stringify({ details: { format: 'gguf' }, capabilities: ['completion'], model_info: { 'fixture.context_length': 32768 } }));
  if (req.url !== '/api/chat') { res.statusCode = 404; return res.end('{}'); }
  let body = ''; for await (const part of req) body += part;
  const payload = JSON.parse(body), context = JSON.parse(payload.messages[1].content); modelPayloads.push(payload);
  let result;
  if (context.snapshot?.schema === 'aicos/public-collaboration/v2') result = { summary: 'Public fixture: preserve the approved scope. <img src="https://example.invalid/leak" onerror="window.companyLeak=1">', actionItems: ['Owner must review the actual receipts.'], citations: [] };
  else if (context.role === 'ba') result = { title: 'Non-empty validation', goals: ['Reject empty string input.'], acceptance: [{ id: 'AC-001', description: 'Empty values fail and valid values pass.', verification: 'test' }], assumptions: [], openQuestions: [] };
  else if (context.role === 'leader') result = { summary: 'Independent code and docs tasks.', tasks: [{ id: 'code', title: 'Fix non-empty validation', agentId: 'dev-agent', dependsOn: [], allowedPaths: ['src/'], acceptanceIds: ['AC-001'] }, { id: 'docs', title: 'Document the validation rule', agentId: 'dev-2', dependsOn: [], allowedPaths: ['docs/'], acceptanceIds: ['AC-001'] }] };
  else if (context.role === 'dev') {
    const path = context.task.id === 'code' ? 'src/validator.cjs' : 'docs/NOTES.md', file = context.repositoryFiles.find(f => f.path === path);
    const hasFailure = Array.isArray(context.feedback) && context.feedback.some(r => r.status === 'failed');
    const hasCorrectCode = file.content.includes('trim().length');
    const content = path.startsWith('src/') ? hasFailure || hasCorrectCode ? "exports.validate = value => typeof value === 'string' && value.trim().length > 0;\n" : "// First attempt deliberately fails the actual test assertion.\nexports.validate = value => value != null;\n" : `Reject empty strings.\nOwner feedback count: ${context.ownerFeedback.length}\n`;
    result = { summary: 'Fixture patch; only runtime receipts prove execution.', files: [{ path, beforeSha256: file.sha256, content }] };
  } else { res.statusCode = 400; return res.end('{}'); }
  if (modelMode === 'invalid') result = { summary: 'wrong software schema' };
  res.end(JSON.stringify({ model, done: true, done_reason: 'stop', message: { role: 'assistant', content: JSON.stringify(result), thinking: 'PRIVATE_COMPANY_THINKING' }, prompt_eval_count: 150, eval_count: 90 }));
});
async function start() {
  await modelServer.listen(0, '127.0.0.1'); await new Promise(r => modelServer.listening ? r() : modelServer.once('listening', r));
  const runtime = process.env.AICOS_RUNTIME_DIR || join(repo, 'src/AICompanyOS.Runtime');
  const dll = process.env.AICOS_RUNTIME_DIR ? join(runtime, 'AICompanyOS.Runtime.dll') : join(runtime, 'bin/Release/net10.0/AICompanyOS.Runtime.dll');
  server = spawn(process.env.AICOS_DOTNET || 'dotnet', [dll], { cwd: runtime, env: { ...process.env, AICOS_DATA_DIR: data, AICOS_OLLAMA_URL: `http://127.0.0.1:${modelServer.address().port}/`, ASPNETCORE_ENVIRONMENT: 'Production' }, stdio: ['ignore', 'pipe', 'pipe'] });
  await new Promise((accept, reject) => {
    let logs = ''; const timer = setTimeout(() => reject(new Error(logs.slice(-2000))), 15000);
    const inspect = chunk => { logs += chunk; if (logs.includes('Now listening on: http://127.0.0.1:4173')) { clearTimeout(timer); accept(); } };
    server.stdout.on('data', inspect); server.stderr.on('data', inspect); server.once('exit', code => { clearTimeout(timer); reject(new Error(`Runtime exit ${code}: ${logs}`)); });
  });
}
async function api(path, body, expected = 200, method = body === undefined ? 'GET' : 'POST') {
  const response = await page.request.fetch(base + path, { method, headers: { Origin: base, 'Idempotency-Key': crypto.randomUUID() }, ...(body === undefined ? {} : { data: body }) });
  check(response.status() === expected, `${method} ${path} expected ${expected}, got ${response.status()}: ${(await response.text()).slice(0, 300)}`);
  return response.headers()['content-type']?.includes('application/json') ? response.json() : response;
}
async function workbench() { return api(cp + '/workbench'); }
async function waitFor(predicate, label, timeout = 90000) {
  const end = Date.now() + timeout; let value;
  while (Date.now() < end) { value = await workbench(); if (predicate(value)) return value; await new Promise(r => setTimeout(r, 500)); }
  throw new Error(`Timeout ${label}: ${JSON.stringify(value?.runs.map(r => ({ state: r.state, nodes: r.nodes, jobs: r.jobs.map(({ input, ...j }) => j), report: r.report }))).slice(-8000)}`);
}
async function submit(id) { await page.locator(`#${id} button[type=submit]`).click(); await page.waitForFunction(() => !document.getElementById('detailDialog').open); }
async function configureAgent(id) {
  await page.click(`[data-studio-agent="${id}"]`); await page.fill('#agentModelId', model); await page.fill('#agentOutputBudget', '2048');
  await page.fill('#agentInputBudget', '10000'); await submit('companyAgentForm');
}
try {
  await mkdir(output, { recursive: true });
  for (const dir of ['src', 'tests', 'docs']) await mkdir(join(source, dir), { recursive: true });
  await writeFile(join(source, 'src/validator.cjs'), 'exports.validate = value => value != null;\n');
  await writeFile(join(source, 'tests/validator.test.cjs'), "const test=require('node:test'), assert=require('node:assert/strict'), {validate}=require('../src/validator.cjs'); test('[AC-001] empty input',()=>{ assert.equal(validate(''),false); assert.equal(validate('valid'),true); });\n");
  await writeFile(join(source, 'docs/NOTES.md'), 'Original notes.\n'); await writeFile(join(source, 'package.json'), '{"name":"company-protocol-fixture","private":true}');
  for (const args of [['init', '-b', 'master'], ['config', 'user.name', 'Fixture'], ['config', 'user.email', 'fixture@localhost'], ['add', '.'], ['commit', '-m', 'Fixture']]) await execFile('git', ['-C', source, ...args]);
  const originalHead = (await execFile('git', ['-C', source, 'rev-parse', 'HEAD'])).stdout.trim();
  await start(); browser = await chromium.launch({ executablePath: process.env.AICOS_CHROMIUM_EXECUTABLE || undefined, headless: true, args: ['--no-sandbox', '--disable-dev-shm-usage'] });
  const context = await browser.newContext({ viewport: { width: 1440, height: 1080 }, acceptDownloads: true }); page = await context.newPage();
  page.on('pageerror', e => errors.push(e.message)); context.on('request', r => { if (/^https?:/.test(r.url()) && new URL(r.url()).origin !== base) outbound.push(r.url()); });
  await page.goto(base); await page.waitForFunction(() => document.getElementById('connection').textContent.includes('Live'));
  await page.selectOption('#companySelect', 'software-studio'); await page.locator('#companyOfficeScene .office-agent').first().waitFor();
  check(await page.locator('#view-company').isVisible(), 'Company must be the default workspace.');
  await page.click('#companySettings'); await page.fill('#companyProfileDescription', 'Build local software with explicit Owner gates.'); await submit('companyProfileForm');
  check((await workbench()).profile.description.includes('Owner gates'), 'Company UI changes persist through the API.');
  await api(cp + '/agents', { agentId: 'dev-2', name: 'Developer Two', roleIds: ['dev'] });
  await page.click('[data-company-tab=studio]'); await page.click('#installCompanySkill'); await submit('installSkillForm');
  for (const id of ['ba-agent', 'leader-agent', 'dev-agent', 'dev-2']) await configureAgent(id);
  check((await workbench()).agentConfigurations.length === 4, 'Per-agent local model routes must be saved.');
  await page.click('#newProject'); await page.fill('#projectId', 'fixture'); await page.fill('#projectName', 'Validation project'); await page.fill('#repositoryPath', source);
  await page.fill('#projectBuild', JSON.stringify({ name: 'build', executable: 'node', arguments: ['--check', 'src/validator.cjs'], timeoutSeconds: 30 }));
  await page.fill('#projectTest', JSON.stringify({ name: 'test', executable: 'node', arguments: ['--test', 'tests/validator.test.cjs'], timeoutSeconds: 30 }));
  await page.check('#projectReadConsent'); await submit('repositoryProjectForm');
  await page.click('[data-company-tab=projects]'); await page.click('[data-project-inventory=fixture]');
  await page.waitForFunction(head => document.getElementById('dialogBody').textContent.includes(head), originalHead);
  check((await page.locator('#dialogBody').innerText()).includes(originalHead), 'UI inventory exposes the exact committed base.'); await page.click('#closeDialog');
  await page.click('#newSoftwareRun'); await page.fill('#softwareObjective', 'Fix empty string validation and document it. Keep all data local.'); await page.check('#softwareReleaseRequested'); await submit('createSoftwareRun');
  let wb = await waitFor(w => w.runs[0]?.state === 'awaiting_plan_approval', 'BA and Leader'); let run = wb.runs[0]; runId = run.seed.runId;
  await page.locator('#software-preview-scope').waitFor();
  await page.fill('#companyCommand', '/trace'); await page.locator('#companyCommandForm button').click(); await page.locator('#detailDialog[open] pre').waitFor();
  check((await page.locator('#dialogBody').innerText()).includes('runtime_context_receiver'), 'Trace displays persisted context handoff ACKs.'); await page.click('#closeDialog');
  check(await page.locator('[data-workflow-node]').count() === 13, 'Executable workflow graph must show its 13 pinned nodes.');
  await api(cp + `/software-runs/${runId}/release`, { expectedSequence: run.lastSequence, candidateSha256: 'wrong', acceptanceConfirmed: true, rightsConfirmed: true }, 409);
  await page.click('#software-revision'); await page.fill('#softwareRevisionFeedback', 'Keep code and docs in independent worktrees.'); await submit('softwareRevisionForm');
  wb = await waitFor(w => w.runs[0]?.state === 'awaiting_plan_approval' && w.runs[0].ownerFeedback.length === 1, 'Owner plan revision'); run = wb.runs[0];
  check(run.jobs.filter(j => j.nodeId === 'leader').length === 2 && run.jobs.at(-1).input.includes('independent worktrees'), 'Owner feedback must create a fresh pinned Leader input.');
  await page.click('[data-company-tab=collaboration]'); await page.click('#sendCompanyMessage'); await page.selectOption('#messageAgent', 'ba-agent'); await page.fill('#messageSummary', 'Please review the accepted scope.'); await submit('companyMessageForm');
  await page.locator('[data-ai-reply]').first().click(); wb = await waitFor(w => w.consultations.some(j => j.status === 'completed'), 'Local public reply');
  check(wb.interactions.some(m => m.origin === 'model_proposal' && m.replyTo), 'Actual local adapter replies are labelled proposals and linked to the public question.');
  await page.click('#companyNewBranch'); await page.fill('#branchName', 'Alternative review'); await page.selectOption('#branchForkMessage', { index: 1 }); await submit('companyBranchForm');
  wb = await workbench(); const branch = wb.branches.find(b => b.name === 'Alternative review');
  check(branch?.parentBranchId === 'main' && branch.forkMessageId, 'Conversation UI persists an immutable parent/message fork boundary.');
  await page.click('#sendCompanyMessage'); await page.selectOption('#messageAgent', 'leader-agent'); await page.fill('#messageSummary', 'Evaluate only this alternative branch.'); await submit('companyMessageForm');
  wb = await workbench(); check(wb.interactions.some(m => m.summary.includes('alternative branch') && m.branchId === branch.branchId), 'New public messages stay in the selected branch.');
  await page.click('#companyCompareModels'); const comparisonRoutes = page.locator('input[name=comparisonRoute]'); await comparisonRoutes.nth(0).check(); await comparisonRoutes.nth(1).check();
  await page.fill('#comparisonQuestion', 'Compare the two bounded approaches.'); await submit('companyComparisonForm');
  wb = await waitFor(w => w.consultations.filter(j => j.comparisonId && j.status === 'completed').length >= 2, 'Same-snapshot model comparison');
  const compared = wb.consultations.filter(j => j.comparisonId === wb.consultations.find(j => j.comparisonId)?.comparisonId);
  check(compared.length === 2 && new Set(compared.map(j => j.contextSnapshotSha256)).size === 1 && new Set(compared.map(j => j.inputSha256)).size === 2, 'Compared routes share one public context snapshot while retaining distinct role/model payloads.');
  await page.waitForFunction(() => document.querySelectorAll('#companyConsultations table tbody tr').length === 2 && document.querySelector('#companyConsultations table')?.textContent.includes('Owner must review the actual receipts'));
  check((await page.locator('#companyConsultations table').innerText()).includes('Owner must review the actual receipts'), 'Comparison table must expose both public answers and action items with per-route usage.');
  await page.screenshot({ path: join(output, 'company-comparison.png'), fullPage: true });

  await page.click('#companySettings'); await page.selectOption('#companyPrivacyMode', 'controlled_cloud'); await submit('companyProfileForm');
  await page.click('[data-company-tab=studio]'); await page.click('#configureCloudProvider'); await page.fill('#cloudProviderId', 'browser-openai'); await page.fill('#cloudProviderModel', 'gpt-6-astra'); await submit('cloudProviderForm');
  await page.click('[data-cloud-credential=browser-openai]'); await page.fill('#cloudCredentialSecret', 'browser-fixture-credential'); await submit('cloudCredentialForm');
  const credential = await api(cp + '/cloud/providers/browser-openai/credential');
  check(credential.available && !JSON.stringify(credential).includes('browser-fixture-credential'), 'Credential UI/API returns availability only.');
  await page.click('[data-cloud-credential=browser-openai]'); await page.click('#clearCloudCredential'); await page.waitForFunction(() => !document.getElementById('detailDialog').open);
  check(!(await api(cp + '/cloud/providers/browser-openai/credential')).available, 'Owner can clear a session credential from Company UI.');
  await page.click('[data-company-tab=collaboration]'); await page.locator('[data-cloud-preview]').first().click(); await page.locator('#cloudPreviewForm button[type=submit]').click();
  await page.waitForFunction(() => document.getElementById('dialogTitle').textContent.includes('Egress manifest'));
  check((await page.locator('#dialogBody').innerText()).includes('api.openai.com/v1/responses') && (await page.locator('#dialogBody').innerText()).includes('"store":false'), 'UI shows the exact stateless payload and pinned cloud endpoint before approval.');
  await page.screenshot({ path: join(output, 'company-cloud-preview.png'), fullPage: true });
  await page.click('#denyEgress'); await page.waitForFunction(() => !document.getElementById('detailDialog').open);
  wb = await workbench(); check(wb.egressManifests.some(m => m.provider.providerId === 'browser-openai' && m.status === 'denied' && m.branchId === branch.branchId), 'Owner denial stays in the selected child branch even for an inherited question and performs no provider request.');
  await page.click('#companyNewMeeting'); await page.fill('#meetingAgenda', 'Review only recorded public constraints.'); await page.selectOption('#meetingParticipants', ['ba-agent', 'leader-agent']); await submit('companyMeetingForm');
  await page.locator('[data-start-meeting]').click(); wb = await waitFor(w => w.meetings[0]?.state === 'started', 'Meeting start'); const meetingId = wb.meetings[0].meetingId;
  await api(cp + '/interactions', { runId, type: 'inform', fromAgentId: 'owner', toAgentIds: ['leader-agent'], summary: 'Record the scope without granting new tools.', artifactRefs: [meetingId], citations: [] });
  await page.locator('[data-summarize-meeting]').click(); wb = await waitFor(w => w.meetings[0]?.state === 'completed', 'Meeting minutes');
  check((await api(cp + `/sources/${meetingId}/revisions/1`)).content.includes('Public fixture'), 'Meeting summary exists as a source artifact, not just animation.');
  check(wb.interactions.every(m => !JSON.stringify(m).includes('PRIVATE_COMPANY_THINKING')), 'Private model thinking must not enter public messages.');
  await page.click('[data-company-tab=office]'); await page.evaluate(() => scrollTo(0, 0)); await page.screenshot({ path: join(output, 'company-office.png'), fullPage: true });
  await page.click('#companyPauseVisual'); check(await page.locator('#companyPauseVisual').getAttribute('aria-pressed') === 'true', 'Visual pause stays independent from workflow controls.');
  await page.check('#companyBoardOnly'); check(await page.locator('#companyOfficeScene').isHidden(), 'Board-only mode is available.'); await page.uncheck('#companyBoardOnly');
  await page.click('[data-company-tab=workflows]'); const capability = await api('/api/v1/execution/capabilities?image=node:22-alpine');
  if (!capability.available) {
    if (process.env.AICOS_REQUIRE_SANDBOX === '1') throw new Error('Required actual sandbox unavailable: ' + capability.reason);
    run = (await workbench()).runs[0]; await api(cp + `/software-runs/${runId}/execution-preview`, { expectedSequence: run.lastSequence }, 409);
    wb = await workbench(); check(wb.runs[0].state === 'awaiting_plan_approval' && !wb.runs[0].jobs.some(j => j.taskId), 'Missing Docker cannot grant worktree execution or invent PASS.');
    await api(cp + `/software-runs/${runId}/export`, undefined, 409);
    console.log('NOT RUN locally: browser container phase requires Linux Docker and preinstalled node:22-alpine; required in Linux CI.');
  } else {
    await page.locator('#software-preview-scope').click(); await page.locator('#approveSoftwareScope').click();
    wb = await waitFor(w => w.runs[0].state === 'awaiting_final_approval', 'Real failing test and corrective cycle', 150000); run = wb.runs[0];
    check(run.round === 1 && run.receipts.some(r => r.tool === 'docker.test' && r.status === 'failed'), 'An actual failing test must drive one corrective Dev round.');
    check(run.receipts.filter(r => r.imageId === capability.imageId && r.status === 'executed').length >= 3, 'Actual container receipts must bind the installed image digest.');
    await page.locator('#software-revision').click(); await page.fill('#softwareRevisionFeedback', 'Add the reviewed scope note to documentation.'); await submit('softwareRevisionForm');
    wb = await waitFor(w => w.runs[0].state === 'awaiting_final_approval' && w.runs[0].round === 2, 'Owner delivery revision', 150000); run = wb.runs[0];
    check(run.candidate.patches.every(p => p.round === 2), 'Final approval refers to newly tested outputs after the Owner revision.');
    await page.locator('#software-final-review').click(); await page.check('#softwareAccept'); await page.check('#softwareRights'); await submit('softwareFinalForm');
    await page.locator('#software-release').waitFor(); await page.locator('#software-release').click(); await page.check('#softwareTargetConfirm'); await submit('softwareReleaseForm');
    wb = await waitFor(w => w.runs[0].state === 'completed', 'Scoped Git commit'); run = wb.runs[0];
    check(run.release.commitSha.length === 40 && !run.release.pushed, 'UI release creates a real scoped commit without an implicit remote push.');
    check((await execFile('git', ['-C', source, 'show', run.release.commitSha + ':src/validator.cjs'])).stdout.includes('trim().length'), 'Git commit contains exactly the corrected source implementation.');
    const downloadPromise = page.waitForEvent('download'); await page.locator('#software-export').click(); const download = await downloadPromise; await download.saveAs(join(output, 'company-source-delivery.zip'));
  }
  check((await execFile('git', ['-C', source, 'rev-parse', 'HEAD'])).stdout.trim() === originalHead && (await readFile(join(source, 'src/validator.cjs'), 'utf8')).includes('value != null'), 'The source checkout and HEAD remain untouched.');
  await page.screenshot({ path: join(output, 'company-workflow.png'), fullPage: true });
  modelMode = 'invalid';
  const invalidRun = await api(cp + '/software-runs', { projectId: 'fixture', objective: 'Malformed output fixture must block' });
  const invalidWorkbench = await waitFor(w => w.runs.some(r => r.seed.runId === invalidRun.seed.runId && r.state === 'blocked'), 'Malformed software model output');
  const blockedRun = invalidWorkbench.runs.find(r => r.seed.runId === invalidRun.seed.runId);
  check(blockedRun.spec === null && blockedRun.report.status === 'blocked' && blockedRun.jobs[0].modelCalls === 1, 'Malformed software model JSON cannot become a spec or a passing workflow.');
  modelMode = 'ok';
  const officeBenchmark = await page.evaluate(async () => {
    const { OfficeScene } = await import('/office.js');
    const svg = document.createElementNS('http://www.w3.org/2000/svg', 'svg'); svg.setAttribute('viewBox', '0 0 940 530'); svg.style.cssText = 'width:900px;height:530px;max-width:100%'; document.body.append(svg);
    const scene = new OfficeScene(svg, () => {}); const roles = ['ba', 'leader', 'dev', 'tester', 'soc', 'qa', 'subpm'];
    const agents = Array.from({ length: 25 }, (_, i) => ({ agent: { agentId: 'benchmark-' + i, name: 'Fixture ' + i, roleIds: [roles[i % roles.length]] }, state: 'idle', sequence: 1, summary: 'Renderer fixture' }));
    scene.update({ agents }); scene.setVisible(true); const before = [...scene.actors.values()].map(a => `${a.cell.c},${a.cell.r}`);
    scene.update({ agents: agents.map(a => ({ ...a, state: 'waiting_approval', sequence: 2 })) });
    const times = []; let previous;
    for (let i = 0; i < 90; i++) await new Promise(resolve => requestAnimationFrame(t => { if (previous !== undefined) times.push(t - previous); previous = t; resolve(); }));
    scene.setPaused(true); const transforms = [...svg.querySelectorAll('.office-agent')].map(g => g.getAttribute('transform'));
    for (let i = 0; i < 3; i++) await new Promise(resolve => requestAnimationFrame(resolve));
    const paused = transforms.every((t, i) => t === svg.querySelectorAll('.office-agent')[i].getAttribute('transform'));
    const after = [...scene.actors.values()].map(a => `${a.cell.c},${a.cell.r}`); times.sort((a, b) => a - b);
    const result = { agents: scene.actors.size, uniqueCells: new Set(after).size, moved: after.filter((p, i) => p !== before[i]).length, frameMeanMs: times.reduce((a, b) => a + b, 0) / times.length, frameP95Ms: times[Math.floor(times.length * .95)], paused };
    scene.setVisible(false); svg.remove(); return result;
  });
  check(officeBenchmark.agents === 25 && officeBenchmark.uniqueCells === 25 && officeBenchmark.moved > 1 && officeBenchmark.paused, 'Actual 25-agent renderer moves concurrently without shared cells and respects visual pause.');
  check(officeBenchmark.frameP95Ms < 250, 'The headless 25-agent fixture must not sustain frame stalls above 250ms.');
  await page.setViewportSize({ width: 390, height: 844 }); await page.click('[data-company-tab=office]');
  check(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1), 'Company workspace must fit mobile width.');
  await page.screenshot({ path: join(output, 'company-mobile.png'), fullPage: true });
  check(errors.length === 0, 'No Company page errors: ' + errors.join('; ')); check(outbound.length === 0 && !await page.evaluate(() => window.companyLeak), 'Workspace text and model replies cannot trigger external browser requests or script injection.');
  check(modelPayloads.length >= 5 && modelPayloads.every(p => !('tools' in p) && p.think === false), 'Real HTTP adapter dispatches pinned typed payloads without model tool grants.');
  await writeFile(join(output, 'company-results.json'), JSON.stringify({ checks, modelCalls: modelPayloads.length, sandbox: capability, officeBenchmark, errors, outbound, limitation: 'Model outputs use a protocol fixture; container phases execute actual code only when Docker is present.' }, null, 2));
  console.log(`PASS: ${checks} Company browser/API assertions; ${modelPayloads.length} local protocol calls; screenshots: ${output}`);
} catch (error) {
  if (page && !page.isClosed()) await page.screenshot({ path: join(output, 'company-failure.png'), fullPage: true }).catch(() => {});
  throw error;
} finally {
  await browser?.close();
  if (server && server.exitCode === null) await new Promise(r => { const timer = setTimeout(() => server.kill('SIGKILL'), 4000); server.once('exit', () => { clearTimeout(timer); r(); }); server.kill('SIGTERM'); });
  await new Promise(r => modelServer.close(r)); await rm(root, { recursive: true, force: true });
}
