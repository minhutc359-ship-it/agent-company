import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { mkdtemp, mkdir, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { createRequire } from 'node:module';
import { request as httpRequest, createServer } from 'node:http';

const repo = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const root = await mkdtemp(join(tmpdir(), 'aicos-e2e-'));
const output = process.env.AICOS_QA_OUTPUT_DIR || await mkdtemp(join(tmpdir(), 'aicos-qa-'));
await mkdir(output, { recursive: true });
const base = 'http://127.0.0.1:4173';
let server, browser, page, cookie, checks = 0, fixtureMode = 'ok', modelCalls = 0;
const waitingResponses = new Set();
const fixtureModel = 'fixture:latest';
const fixtureArtifact = { title: 'Local document', summary: 'Produced by protocol fixture', files: [{ path: 'index.html', content: '<html lang="vi"><head><meta charset="utf-8"/><title>Local document</title><style>body{font-family:system-ui;padding:2rem;color:#173b35}</style></head><body><main><h1>Local document</h1><p>Keep data local.</p></main></body></html>' }] };
const modelServer = createServer(async (req, res) => {
  res.setHeader('Content-Type', 'application/json');
  if (req.url === '/api/tags') return res.end(JSON.stringify({ models: [{ name: fixtureModel, digest: 'a'.repeat(64), details: { format: 'gguf', parameter_size: 'fixture' } }, { name: 'remote:cloud', remote_host: 'https://external.invalid', digest: 'b'.repeat(64), details: { format: 'gguf' } }] }));
  if (req.url === '/api/show') return res.end(JSON.stringify({ details: { format: 'gguf' }, capabilities: ['completion'], model_info: { 'test.context_length': 32768 } }));
  if (req.url !== '/api/chat') { res.statusCode = 404; return res.end('{}'); }
  let rawBody = ''; for await (const part of req) { rawBody += part; if (rawBody.length > 65536) { res.statusCode = 413; return res.end('{}'); } }
  const payload = JSON.parse(rawBody), context = JSON.parse(payload.messages[1].content);
  modelCalls++;
  if (fixtureMode === 'hang') { waitingResponses.add(res); res.on('close', () => waitingResponses.delete(res)); return; }
  const content = context.roleId === 'dev' ? JSON.stringify(fixtureArtifact) : fixtureMode === 'invalid' ? '{"summary":"wrong schema"}' : JSON.stringify({ summary: `${context.roleId.toUpperCase()} protocol fixture <img src="https://example.invalid/model-leak" onerror="window.fixtureLeak=1">`, deliverables: ['Preserve approved constraints', 'Ask Owner before implementation'], questions: [] });
  res.end(JSON.stringify({ model: fixtureModel, done: true, done_reason: 'stop', message: { role: 'assistant', content, thinking: 'PRIVATE_THINKING_MARKER' }, prompt_eval_count: 123, eval_count: 23 }));
});
await new Promise(resolve => modelServer.listen(0, '127.0.0.1', resolve));
const modelEndpoint = `http://127.0.0.1:${modelServer.address().port}/`;
const completed = [];
function check(condition, message) { assert.ok(condition, message); checks++; }
async function startServer() {
  const configuration = process.env.AICOS_CONFIGURATION || 'Release';
  const runtime = process.env.AICOS_RUNTIME_DIR || join(repo, 'src/AICompanyOS.Runtime');
  const dll = process.env.AICOS_RUNTIME_DIR ? join(runtime, 'AICompanyOS.Runtime.dll') : join(runtime, `bin/${configuration}/net10.0/AICompanyOS.Runtime.dll`);
  server = spawn(process.env.AICOS_DOTNET || 'dotnet', [dll], {
    cwd: runtime, env: { ...process.env, AICOS_DATA_DIR: root, AICOS_OLLAMA_URL: modelEndpoint, ASPNETCORE_ENVIRONMENT: 'Production', DOTNET_CLI_TELEMETRY_OPTOUT: '1' }, stdio: ['ignore', 'pipe', 'pipe']
  });
  await new Promise((accept, reject) => {
    let logs = '';
    const timer = setTimeout(() => reject(new Error(`Isolated test runtime did not start. ${logs.slice(-1500)}`)), 15000);
    const inspect = chunk => { logs += chunk; if (logs.includes('Now listening on: http://127.0.0.1:4173')) { clearTimeout(timer); accept(); } };
    server.stdout.on('data', inspect); server.stderr.on('data', inspect);
    server.once('exit', code => { clearTimeout(timer); reject(new Error(`Isolated runtime exited (${code}). Ensure port 4173 is free. ${logs.slice(-1500)}`)); });
  });
}
async function stopServer(force = false) {
  if (!server || server.exitCode !== null) return;
  const process = server;
  await new Promise(resolve => { const timer = setTimeout(() => process.kill('SIGKILL'), 3000); process.once('exit', () => { clearTimeout(timer); resolve(); }); process.kill(force ? 'SIGKILL' : 'SIGTERM'); });
}
async function raw(path, { body, method = 'GET', origin = base, authenticated = true, headers = {} } = {}) {
  if (headers.Host) return new Promise((resolve, reject) => {
    const req = httpRequest(base + path, { method, headers: { ...headers, ...(cookie ? { Cookie: cookie } : {}) } }, response => {
      const chunks = []; response.on('data', chunk => chunks.push(chunk));
      response.on('end', () => resolve(new Response(Buffer.concat(chunks), { status: response.statusCode, headers: response.headers })));
    });
    req.on('error', reject); req.end();
  });
  return fetch(base + path, { method, redirect: 'manual', headers: { ...(authenticated && cookie ? { Cookie: cookie } : {}), ...(origin ? { Origin: origin } : {}), ...(body !== undefined ? { 'Content-Type': 'application/json' } : {}), ...headers }, body: body === undefined ? undefined : JSON.stringify(body) });
}
async function call(path, options = {}, expected = 200) {
  const response = await raw(path, options); check(response.status === expected, `${options.method || 'GET'} ${path}: expected ${expected}, got ${response.status}`);
  return response.headers.get('Content-Type')?.includes('application/json') ? response.json() : response;
}
async function session() {
  const response = await raw('/api/session', { authenticated: false }); check(response.status === 200, 'Session bootstrap must succeed locally.');
  const header = response.headers.get('set-cookie'); check(header.includes('httponly') && header.includes('samesite=strict'), 'Session cookie must have HttpOnly and SameSite Strict.');
  cookie = header.split(';')[0];
}
async function sseFirst(afterSeq, lastId) {
  const controller = new AbortController(), timeout = setTimeout(() => controller.abort(), 5000);
  try {
    const response = await fetch(`${base}/api/v1/companies/api-fixture/events/stream?afterSeq=${afterSeq}`, { headers: { Cookie: cookie, ...(lastId !== undefined ? { 'Last-Event-ID': String(lastId) } : {}) }, signal: controller.signal });
    check(response.status === 200 && response.headers.get('Content-Type').includes('text/event-stream'), 'Authenticated SSE must connect.');
    const reader = response.body.getReader(), decoder = new TextDecoder(); let text = '';
    while (true) {
      const chunk = await reader.read(); if (chunk.done) throw new Error('SSE ended before replay.');
      text += decoder.decode(chunk.value, { stream: true });
      const match = /id: (\d+)\nevent: workspace\ndata: ([^\n]+)/.exec(text);
      if (match) { const item = JSON.parse(match[2]); check(item.seq === Number(match[1]), 'SSE event ID and payload cursor must agree.'); return item; }
    }
  } finally { clearTimeout(timeout); controller.abort(); }
}
async function apiChecks() {
  await call('/api/v1/health', { authenticated: false, origin: null }, 401);
  await call('/api/session', { authenticated: false, origin: 'https://untrusted.invalid' }, 403);
  await session();
  const health = await raw('/api/v1/health');
  check(health.headers.get('content-security-policy').includes("script-src 'self'") && !health.headers.get('content-security-policy').includes('unsafe-inline'), 'Strict CSP must disallow inline code.');
  check(health.headers.get('cache-control') === 'no-store', 'Workspace responses must not be cached.');
  await call('/api/v1/health', { headers: { Host: 'evil.invalid:4173' } }, 403);
  await call('/api/v1/companies', { method: 'POST', origin: null, body: { companyId: 'blocked', name: 'Blocked' } }, 403);
  await call('/api/v1/companies', { method: 'POST', origin: 'https://untrusted.invalid', body: { companyId: 'blocked', name: 'Blocked' } }, 403);
  await call('/api/v1/companies', { method: 'POST', body: { companyId: 'api-fixture', name: 'API Fixture' } });
  await call('/api/v1/companies/unknown/workflow-runs', {}, 404);
  await call('/api/v1/companies/__profile/workflow-runs', {}, 404);
  const path = '/api/v1/companies/api-fixture';
  await call(`${path}/workflow-runs`, { method: 'POST', body: { objective: 'No key' } }, 400);
  const run = await call(`${path}/workflow-runs`, { method: 'POST', body: { objective: 'HTTP durability', sourceRefs: [] }, headers: { 'Idempotency-Key': 'http-create' } }, 201);
  const duplicate = await call(`${path}/workflow-runs`, { method: 'POST', body: { objective: 'HTTP durability', sourceRefs: [] }, headers: { 'Idempotency-Key': 'http-create' } });
  check(run.runId === duplicate.runId && run.lastSequence === duplicate.lastSequence, 'HTTP retry must not double-create.');
  await call(`${path}/workflow-runs`, { method: 'POST', body: { objective: 'Changed payload' }, headers: { 'Idempotency-Key': 'http-create' } }, 409);
  await call(`${path}/workflow-runs/${run.runId}/handoff/ack`, { method: 'POST', body: { receiverAgentId: 'leader-agent', packetHash: 'sha256:wrong' }, headers: { 'Idempotency-Key': 'bad-ack' } }, 422);
  check((await sseFirst(1)).seq === 2, 'SSE must replay after the query cursor.');
  check((await sseFirst(0, 2)).seq === 3, 'Last-Event-ID must override the query cursor on reconnect.');
  const ack = await call(`${path}/workflow-runs/${run.runId}/handoff/ack`, { method: 'POST', body: { receiverAgentId: 'leader-agent', packetHash: run.handoff.contentSha256, expectedSequence: run.lastSequence }, headers: { 'Idempotency-Key': 'http-ack' } });
  const decision = await call(`${path}/workflow-runs/${run.runId}/approval`, { method: 'POST', body: { approved: true, expectedSequence: ack.lastSequence }, headers: { 'Idempotency-Key': 'http-approve' } });
  check(decision.state === 'approved', 'Owner must be able to approve the scaffold plan.');
  const report = await call(`${path}/workflow-runs/${run.runId}/report`);
  check(report.evidenceRefs.length === 0, 'Source labels cannot be execution receipts.');
  await call(`${path}/sources`, { method: 'POST', body: { sourceId: 'credentials', title: '.env', content: 'api_key=sk-proj-1234567890123456789012345' } }, 400);
  const agent = await call(`${path}/agents`, { method: 'POST', body: { agentId: 'no-escalation', name: 'No escalation', roleIds: ['dev', 'tester'], toolGrants: ['shell.execute'] } });
  check(agent.toolGrants.length === 0, 'Client-supplied tool grants must not be accepted through agent creation.');
  completed.push('HTTP session, Host/Origin/CSRF, idempotency, scoped access, ACK hash and SSE replay');
}
async function waitRun(path, predicate) {
  const deadline = Date.now() + 15000; let run;
  while (Date.now() < deadline) {
    const response = await raw(path); assert.equal(response.status, 200);
    run = await response.json(); if (predicate(run)) return run;
    await new Promise(resolve => setTimeout(resolve, 100));
  }
  throw new Error(`Workflow did not reach expected state: ${JSON.stringify({ state: run?.state, lastError: run?.lastError })}`);
}
async function modelChecks() {
  check(modelCalls === 0, 'Scaffold operations and startup must never generate a model response.');
  const models = await call('/api/v1/providers/local/models');
  check(models.length === 1 && models[0].name === fixtureModel, 'Only installed local models should appear.');
  await call('/api/v1/companies', { method: 'POST', body: { companyId: 'model-fixture', name: 'Model Fixture' } });
  const basePath = '/api/v1/companies/model-fixture/workflow-runs';
  const created = await call(basePath, { method: 'POST', headers: { 'Idempotency-Key': 'local-create' }, body: { objective: 'Local BA to Leader', executionMode: 'local_model', modelId: fixtureModel, sourceRefs: [] } }, 201);
  const path = `${basePath}/${created.runId}`;
  let run = await waitRun(path, r => r.state === 'awaiting_handoff' || r.state === 'blocked');
  check(run.state === 'awaiting_handoff' && run.spec.verificationState === 'produced_by_model', 'BA must produce a typed model output before handoff.');
  const duplicate = await call(basePath, { method: 'POST', headers: { 'Idempotency-Key': 'local-create' }, body: { objective: 'Local BA to Leader', executionMode: 'local_model', modelId: fixtureModel, sourceRefs: [] } });
  check(duplicate.runId === run.runId && modelCalls === 1, 'Create replay must not regenerate BA output.');
  await call(`${path}/handoff/ack`, { method: 'POST', headers: { 'Idempotency-Key': 'local-ack' }, body: { packetHash: run.handoff.contentSha256, receiverAgentId: 'leader-agent', expectedSequence: run.lastSequence } });
  run = await waitRun(path, r => r.state === 'awaiting_approval' || r.state === 'blocked');
  check(run.state === 'awaiting_approval' && run.invocations.length === 2 && run.invocations.every(j => j.usage.outputTokens === 23), 'Leader must receive BA output and persist provider usage.');
  run = await call(`${path}/approval`, { method: 'POST', headers: { 'Idempotency-Key': 'local-approve' }, body: { approved: true, expectedSequence: run.lastSequence } });
  const report = await call(`${path}/report`);
  check(report.modelInvocations.length === 2 && report.evidenceRefs.length === 0 && !JSON.stringify(report).includes('PRIVATE_THINKING_MARKER'), 'Report must separate model output from test evidence and discard thinking.');
  const deliveryPath = `${path}/delivery`;
  await call(`${deliveryPath}/export`, {}, 409);
  const preparation = { expectedSequence: run.lastSequence, mode: 'local_model', criteria: [{ id: 'privacy', path: 'index.html', contains: 'Keep data local.' }] };
  await call(`${deliveryPath}/prepare`, { method: 'POST', origin: null, headers: { 'Idempotency-Key': 'denied-dev' }, body: preparation }, 403);
  await call(`${deliveryPath}/prepare`, { method: 'POST', headers: { 'Idempotency-Key': 'stale-dev' }, body: { ...preparation, expectedSequence: 0 } }, 409);
  await call(`${deliveryPath}/prepare`, { method: 'POST', headers: { 'Idempotency-Key': 'prepare-dev' }, body: preparation });
  run = await waitRun(path, r => ['awaiting_delivery_approval', 'delivery_blocked', 'blocked'].includes(r.state));
  check(run.state === 'awaiting_delivery_approval' && run.invocations.length === 3 && run.delivery.receipts.length === 3 && run.delivery.receipts.every(r => r.exitCode === 0), 'Real HTTP Dev output must create exact artifacts and three passing tool receipts.');
  const preview = await raw(`${deliveryPath}/preview`);
  check(preview.status === 200 && preview.headers.get('content-security-policy').includes('sandbox') && preview.headers.get('content-security-policy').includes("connect-src 'none'") && (await preview.text()).includes('Keep data local.'), 'Preview must have its own network/script-denying sandbox policy.');
  const review = { expectedSequence: run.lastSequence, artifactSha256: run.delivery.artifact.sha256, approved: true, acceptanceConfirmed: true, rightsConfirmed: false };
  await call(`${deliveryPath}/review`, { method: 'POST', headers: { 'Idempotency-Key': 'missing-rights' }, body: review }, 409);
  run = await call(`${deliveryPath}/review`, { method: 'POST', headers: { 'Idempotency-Key': 'accept-delivery' }, body: { ...review, rightsConfirmed: true } });
  check(run.state === 'delivered' && run.delivery.report.status === 'owner_accepted', 'Final human acceptance must be independent of plan approval and gate PASS.');
  const archive = await raw(`${deliveryPath}/export`), archiveBytes = new Uint8Array(await archive.arrayBuffer());
  check(archive.status === 200 && archive.headers.get('content-type') === 'application/zip' && archiveBytes[0] === 0x50 && archiveBytes[1] === 0x4b, 'Accepted files and receipts must export as a real ZIP.');
  const finalReport = await call(`${path}/report`);
  check(finalReport.evidenceRefs.length === 3 && finalReport.modelInvocations.length === 3 && !JSON.stringify(finalReport).includes('PRIVATE_THINKING_MARKER'), 'Delivery report must link actual check evidence and discard hidden reasoning.');
  fixtureMode = 'invalid';
  let failing = await call(basePath, { method: 'POST', headers: { 'Idempotency-Key': 'invalid-local' }, body: { objective: 'Reject schema violation', executionMode: 'local_model', modelId: fixtureModel } }, 201);
  const failingPath = `${basePath}/${failing.runId}`;
  failing = await waitRun(failingPath, r => r.state === 'blocked');
  check(failing.lastError === 'MODEL_OUTPUT_SCHEMA_INVALID', 'Malformed provider output must block the real worker.');
  fixtureMode = 'ok';
  await call(`${failingPath}/retry`, { method: 'POST', headers: { 'Idempotency-Key': 'retry-local' }, body: { expectedSequence: failing.lastSequence } });
  failing = await waitRun(failingPath, r => r.state === 'awaiting_handoff');
  check(failing.invocations[0].attempts === 2, 'Retry must use its remaining attempt.');
  fixtureMode = 'hang';
  let interrupted = await call(basePath, { method: 'POST', headers: { 'Idempotency-Key': 'crash-local' }, body: { objective: 'Recover interrupted generation', executionMode: 'local_model', modelId: fixtureModel } }, 201);
  const interruptedPath = `${basePath}/${interrupted.runId}`;
  await waitRun(interruptedPath, r => r.state === 'running');
  const deadline = Date.now() + 5000;
  while (!waitingResponses.size && Date.now() < deadline) await new Promise(resolve => setTimeout(resolve, 50));
  check(waitingResponses.size > 0, 'Crash fixture must interrupt an actual in-flight HTTP generation.');
  await stopServer(true); fixtureMode = 'ok'; await startServer();
  await call('/api/v1/health', {}, 401); await session();
  interrupted = await waitRun(interruptedPath, r => r.state === 'blocked');
  check(interrupted.lastError === 'RESTART_REQUIRES_RETRY' && interrupted.invocations[0].attempts === 1, 'Killed runtime must recover the lease without blind re-execution.');
  await call(`${interruptedPath}/retry`, { method: 'POST', headers: { 'Idempotency-Key': 'recover-local' }, body: { expectedSequence: interrupted.lastSequence } });
  interrupted = await waitRun(interruptedPath, r => r.state === 'awaiting_handoff');
  check(interrupted.invocations[0].attempts === 2, 'Recovered invocation must finish only after explicit bounded retry.');
  completed.push('Local Ollama protocol fixture: BA/Leader/Owner, usage, schema rejection, bounded retry and SIGKILL during in-flight generation');
}
async function browserChecks() {
  const require = createRequire(import.meta.url), { chromium } = require(process.env.AICOS_PLAYWRIGHT_MODULE || 'playwright');
  browser = await chromium.launch({ executablePath: process.env.AICOS_CHROMIUM_EXECUTABLE || undefined, headless: true, args: ['--no-sandbox', '--disable-dev-shm-usage'] });
  const context = await browser.newContext({ viewport: { width: 1440, height: 1080 }, acceptDownloads: true });
  page = await context.newPage(); const errors = [], outbound = [];
  page.on('pageerror', e => errors.push(e.message));
  page.on('console', msg => { if (msg.type() === 'error') errors.push(msg.text()); });
  context.on('request', request => { if (/^https?:/.test(request.url()) && new URL(request.url()).origin !== base) outbound.push(request.url()); });
  await page.goto(base); await page.waitForFunction(() => document.getElementById('connection').textContent.includes('Live'));
  await page.selectOption('#companySelect', 'software-studio'); await page.click('[data-view=work]');
  await page.fill('#objective', 'Kiểm tra giao việc và checkpoint');
  await page.locator('#createRun button[type=submit]').click();
  await page.locator('#run-pause').waitFor(); await page.locator('#run-pause').click();
  await page.waitForFunction(() => document.getElementById('runState').textContent === 'Đã tạm dừng');
  await page.reload(); await page.waitForFunction(() => document.getElementById('connection').textContent.includes('Live'));
  await page.selectOption('#companySelect', 'software-studio'); await page.click('[data-view=work]');
  await page.locator('#run-resume').waitFor(); await page.locator('#run-resume').click();
  await page.locator('#run-ack').waitFor(); await page.locator('#run-ack').click();
  await page.locator('#run-approve').waitFor(); await page.locator('#run-approve').click();
  await page.waitForFunction(() => document.getElementById('runState').textContent === 'Plan đã duyệt');
  await page.locator('#run-report').click(); await page.locator('#detailDialog').waitFor();
  check((await page.locator('#dialogBody').innerText()).includes('Execution receipts: 0'), 'UI report must disclose absence of executed tools.');
  await page.click('#closeDialog');
  await page.click('#prepare-delivery');
  await page.locator('#prepareDeliveryForm button[type=submit]').click();
  await page.waitForFunction(() => document.getElementById('runState').textContent === 'Chờ file sản phẩm');
  await page.click('#submit-delivery'); await page.locator('#artifactForm button[type=submit]').click();
  await page.waitForFunction(() => document.getElementById('runState').textContent === 'Chờ duyệt bàn giao');
  check(await page.locator('#deliveryBody details').count() === 3, 'Manual delivery must run all static checks and display their receipts.');
  await page.click('#preview-delivery');
  await page.frameLocator('#deliveryPreview').locator('h1').waitFor();
  check((await page.frameLocator('#deliveryPreview').locator('h1').innerText()).includes('Tài liệu dự án') && await page.locator('#deliveryPreview').getAttribute('sandbox') === '', 'Isolated preview must render only the static artifact.');
  await page.click('#closeDialog'); await page.click('#reject-delivery');
  await page.waitForFunction(() => document.getElementById('runState').textContent === 'Cần sửa sản phẩm');
  await page.click('#submit-delivery'); await page.locator('#artifactForm button[type=submit]').click();
  await page.waitForFunction(() => document.getElementById('runState').textContent === 'Chờ duyệt bàn giao');
  await page.click('#review-delivery'); await page.check('#deliveryAcceptance'); await page.check('#deliveryRights');
  await page.locator('#deliveryReviewForm button[type=submit]').click();
  await page.waitForFunction(() => document.getElementById('runState').textContent === 'Đã bàn giao');
  const deliveryDownloadPromise = page.waitForEvent('download'); await page.click('#export-delivery');
  const deliveryDownload = await deliveryDownloadPromise; await deliveryDownload.saveAs(join(output, 'manual-delivery.zip'));
  check(deliveryDownload.suggestedFilename().endsWith('-delivery.zip'), 'Owner-approved manual package must download as ZIP.');
  await page.click('[data-view=knowledge]');
  const importText = 'Chỉ dùng SQLite local. Không tự push.\n<img src="https://example.invalid/leak" onerror="window.xss=1">';
  await page.setInputFiles('#sourceImportFile', { name: 'Local policy.txt', mimeType: 'text/plain', buffer: Buffer.from(importText, 'utf8') });
  await page.waitForFunction(() => document.getElementById('sourceText').value.includes('SQLite'));
  check(await page.inputValue('#sourceText') === importText, 'UTF-8 file import must preserve exact text before submit.');
  await page.fill('#sourceTitle', 'Local policy');
  await page.locator('#importSource button[type=submit]').click();
  await page.waitForFunction(() => document.getElementById('sourceList').textContent.includes('Local policy'));
  await page.fill('#searchQuery', 'SQLite'); await page.locator('#searchForm button').click();
  await page.locator('[data-propose]').waitFor();
  check(await page.locator('#searchResults img').count() === 0 && await page.evaluate(() => window.xss) === undefined, 'Imported HTML must render as plain text.');
  await page.click('[data-propose]'); await page.fill('#memoryBody', 'Chỉ dùng SQLite local; không tự push.');
  await page.locator('#proposalForm button[type=submit]').click();
  await page.locator('[data-review][data-accepted=true]').waitFor(); await page.click('[data-review][data-accepted=true]');
  await page.waitForFunction(() => document.getElementById('canonical').textContent.includes('Chỉ dùng SQLite local; không tự push.'));
  await page.click('[data-view=organization]'); await page.fill('#agentName', 'BA + Leader fixture');
  await page.locator('#newAgentRoles input[value=leader]').check(); await page.locator('#createAgent button[type=submit]').click();
  await page.waitForFunction(() => document.getElementById('agentRegistry').textContent.includes('BA + Leader fixture'));
  await page.click('[data-view=office]'); await page.locator('#officeScene .office-agent').first().waitFor();
  const before = await (await context.request.get(`${base}/api/v1/companies/software-studio/events`)).json();
  await page.click('#pauseVisual');
  const after = await (await context.request.get(`${base}/api/v1/companies/software-studio/events`)).json();
  check(before.length === after.length, 'Pause Visual must not issue a workflow command.');
  check(await page.locator('#officeScene .office-agent').count() === 8, 'Office must project all registered agents.');
  await page.evaluate(() => scrollTo(0, 0));
  await page.screenshot({ path: join(output, 'desktop-office.png'), fullPage: true });
  await page.click('[data-view=storage]'); await page.click('#verifyStorage');
  await page.waitForFunction(() => document.getElementById('integrityResult').textContent.includes('events'));
  await page.selectOption('#targetBackend', 'sqlite'); await page.locator('#migrateStorage button[type=submit]').click();
  await page.waitForFunction(() => document.getElementById('storageBadge').textContent.includes('SQLite'));
  await page.fill('#backupPassphrase', 'ui-backup-passphrase-123');
  const downloadPromise = page.waitForEvent('download'); await page.locator('#backupForm button[type=submit]').click();
  const download = await downloadPromise, archive = join(output, 'fixture-backup.aicos'); await download.saveAs(archive);
  await page.setInputFiles('#backupFile', archive); await page.fill('#validatePassphrase', 'ui-backup-passphrase-123');
  await page.locator('#validateBackup button[type=submit]').click();
  await page.waitForFunction(() => document.getElementById('backupResult').textContent.includes('Giải mã và checksum hợp lệ'));
  await page.click('#addCompany'); await page.fill('#companyName', 'Empty UI company'); await page.fill('#companyId', 'ui-empty');
  await page.locator('#companyForm button[type=submit]').click();
  await page.waitForFunction(() => document.getElementById('companySelect').value === 'ui-empty' && document.getElementById('canonical').textContent.includes('Chưa có bộ nhớ'));
  await page.click('[data-view=knowledge]');
  check(!(await page.locator('#sourceList').innerText()).includes('Local policy'), 'Switching company must clear prior company data.');
  await page.selectOption('#companySelect', 'software-studio'); await page.click('[data-view=work]'); await page.click('[data-view=work]');
  await page.emulateMedia({ reducedMotion: 'reduce' }); await page.setViewportSize({ width: 390, height: 844 });
  await page.evaluate(() => scrollTo(0, 0));
  await page.screenshot({ path: join(output, 'mobile-workspace.png'), fullPage: true });
  check(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 2), 'Mobile layout must not overflow horizontally.');
  await page.setViewportSize({ width: 1440, height: 1080 });
  await page.selectOption('#executionMode', 'local_model'); await page.click('#loadModels');
  await page.waitForFunction(() => document.querySelector('#localModel option')?.value === 'fixture:latest');
  await page.fill('#objective', 'Thực hiện BA và Leader bằng AI local');
  await page.locator('#createRun button[type=submit]').click();
  await page.waitForFunction(() => document.getElementById('runState').textContent === 'Chờ bàn giao');
  check(await page.locator('#invocations .record').count() === 1 && await page.locator('#invocations img').count() === 0, 'Model output must be visible and HTML-escaped.');
  await page.click('#run-ack'); await page.waitForFunction(() => document.getElementById('runState').textContent === 'Chờ Owner');
  check(await page.locator('#invocations .record').count() === 2 && (await page.locator('#invocations').innerText()).includes('output 23'), 'UI must show both local model receipts and usage.');
  await page.click('#run-approve'); await page.waitForFunction(() => document.getElementById('runState').textContent === 'Plan đã duyệt');
  await page.locator('#invocations .record button').first().click();
  check((await page.locator('#dialogBody').innerText()).includes('ai-company-os/role-context/v1'), 'Invocation inspector must expose the pinned role context.');
  await page.click('#closeDialog'); await page.evaluate(() => scrollTo(0, 0));
  await page.screenshot({ path: join(output, 'local-inference-workspace.png'), fullPage: true });
  await page.setViewportSize({ width: 390, height: 844 });
  check(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 2), 'Mobile inference controls and output cards must not overflow.');
  await page.screenshot({ path: join(output, 'mobile-local-inference.png'), fullPage: true });
  await page.setViewportSize({ width: 1440, height: 1080 });
  await page.click('#prepare-delivery'); await page.selectOption('#deliveryMode', 'local_model');
  await page.fill('#deliveryCriteria', 'index.html | Keep data local.');
  await page.locator('#prepareDeliveryForm button[type=submit]').click();
  await page.waitForFunction(() => document.getElementById('runState').textContent === 'Chờ duyệt bàn giao');
  check(await page.locator('#invocations .record').count() === 3 && await page.locator('#deliveryBody details').count() === 3, 'UI must execute and expose the full local AI Dev/check/report pipeline.');
  await page.click('#review-delivery'); await page.check('#deliveryAcceptance'); await page.check('#deliveryRights');
  await page.locator('#deliveryReviewForm button[type=submit]').click();
  await page.waitForFunction(() => document.getElementById('runState').textContent === 'Đã bàn giao');
  await page.locator('#deliveryPanel').scrollIntoViewIfNeeded(); await page.screenshot({ path: join(output, 'v1-delivery-desktop.png'), fullPage: true });
  await page.setViewportSize({ width: 390, height: 844 });
  check(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 2), 'Delivery checks, artifact hashes and final controls must fit mobile.');
  await page.screenshot({ path: join(output, 'v1-delivery-mobile.png'), fullPage: true });
  check(errors.length === 0, `Browser errors: ${errors.join('; ')}`);
  check(outbound.length === 0, `Application made external browser requests: ${outbound.join('; ')}`);
  completed.push('Browser create/pause/reload/resume/ACK/approve, escaped source retrieval, reviewed memory, multi-role agents, office projection, migration, encrypted backup, mobile and no external browser requests');
  await browser.close(); browser = null;
}
try {
  await startServer(); await apiChecks(); await modelChecks();
  if (!process.argv.includes('--api-only')) await browserChecks();
  await stopServer(); await startServer();
  await call('/api/v1/health', {}, 401); // Cookie from previous process must be invalid now.
  await session();
  const view = await call('/api/v1/companies/api-fixture/workspace/snapshot');
  check(view.runs.some(run => run.objective === 'HTTP durability' && run.state === 'approved'), 'Restart must preserve the HTTP workflow.');
  const delivered = await call('/api/v1/companies/model-fixture/workflow-runs');
  check(delivered.some(run => run.state === 'delivered' && run.delivery.receipts.length === 3), 'Accepted delivery must survive a real process restart.');
  completed.push('Real process restart: old session rejected, persisted workflow and accepted delivery restored');
  const result = { checks, completed, qaOutput: output };
  await writeFile(join(output, 'verification.json'), JSON.stringify(result, null, 2));
  console.log(`PASS: ${checks} API/browser assertions. Evidence: ${output}`);
} catch (error) {
  if (page && !page.isClosed()) await page.screenshot({ path: join(output, 'failure.png'), fullPage: true }).catch(() => {});
  console.error(error); process.exitCode = 1;
} finally { await browser?.close(); await stopServer(); await new Promise(resolve => modelServer.close(resolve)); await rm(root, { recursive: true, force: true }); }
