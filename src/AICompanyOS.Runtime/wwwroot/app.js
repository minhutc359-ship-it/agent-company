import { OfficeScene } from './office.js';
import { renderDelivery } from './delivery.js';
import { CompanyWorkbench } from './workbench.js';
const $ = id => document.getElementById(id);
const escape = value => String(value ?? '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[c]);
const uid = prefix => `${prefix}-${crypto.randomUUID()}`;
const empty = message => `<div class="empty">${escape(message)}</div>`;
const titles = { company: ['Công ty AI của bạn.', 'Từ yêu cầu đến patch, kiểm chứng và bàn giao trong một workspace.'],
  work: ['Điều phối công việc.', 'Từ yêu cầu đến bàn giao. Mỗi bước đều có dấu vết.'],
  office: ['Một văn phòng, một nguồn thật.', 'Trạng thái nhân viên được chiếu từ sự kiện đã lưu.'],
  knowledge: ['Kiến thức có nguồn gốc.', 'Lưu tài liệu, đối chiếu trích dẫn và duyệt bộ nhớ chính thức.'],
  organization: ['Đúng người. Đúng vai trò.', 'Một agent có thể nhận nhiều vai trò; quyền công cụ được quản lý riêng.'],
  storage: ['Dữ liệu do bạn làm chủ.', 'Lưu trên thiết bị, kiểm tra toàn vẹn và sao lưu có mã hóa.']
};
const labels = { delivered: 'Đã bàn giao', awaiting_delivery_input: 'Chờ file sản phẩm', awaiting_delivery_approval: 'Chờ duyệt bàn giao', delivery_blocked: 'Kiểm tra chưa đạt', delivery_revision_requested: 'Cần sửa sản phẩm', checking_delivery: 'Đang kiểm tra', queued: 'Đang chờ AI', running: 'AI local đang xử lý', working: 'AI local đang xử lý', interrupted: 'Cần retry', failed: 'Thất bại', completed: 'Đã lưu output', awaiting_handoff: 'Chờ bàn giao', awaiting_approval: 'Chờ Owner', approved: 'Plan đã duyệt', revision_requested: 'Cần sửa kế hoạch', paused: 'Đã tạm dừng', cancelled: 'Đã hủy', idle: 'Chưa có invocation', handoff_pending: 'Chờ ACK', waiting_approval: 'Chờ Owner', blocked: 'Cần sửa lại' };
const eventNames = { 'delivery.prepared': 'Owner cho phép tạo gói file', 'delivery.artifact.produced': 'Dev đã tạo artifact', 'delivery.check.executed': 'Đã chạy static check', 'delivery.report.produced': 'SubPM tổng hợp receipts', 'delivery.reviewed': 'Owner quyết định bàn giao', 'delivery.command': 'Ghi checkpoint bàn giao', 'invocation.queued': 'Xếp hàng AI local', 'invocation.started': 'Bắt đầu gọi AI local', 'invocation.heartbeat': 'Gia hạn lease', 'invocation.completed': 'Đã lưu output AI', 'invocation.failed': 'AI bị chặn hoặc thất bại', 'invocation.interrupted': 'Invocation bị gián đoạn', 'invocation.retry_requested': 'Yêu cầu retry AI', 'workflow.created': 'Tạo workflow', 'spec.produced': 'Lưu spec', 'handoff.requested': 'Yêu cầu bàn giao', 'handoff.acknowledged': 'Ghi nhận ACK', 'plan.produced': 'Lưu plan', 'approval.required': 'Chờ Owner duyệt', 'owner.approval.decided': 'Owner đã quyết định', 'workflow.paused': 'Tạm dừng workflow', 'workflow.resumed': 'Tiếp tục workflow', 'workflow.cancelled': 'Hủy workflow', 'agent.created': 'Thêm agent', 'agent.roles.changed': 'Cập nhật vai trò', 'source.imported': 'Lưu phiên bản nguồn', 'memory.proposed': 'Đề xuất bộ nhớ', 'memory.accepted': 'Duyệt bộ nhớ', 'memory.rejected': 'Từ chối bộ nhớ' };
let companyId = null, companies = [], roles = [], state = {}, selectedRunId = null, view = 'company';
let cursor = 0, stream = null, streamGeneration = 0, refreshVersion = 0, refreshTimer, retryTimer;
let createKey = uid('create'), sourceId = uid('source'), agentId = uid('agent'), manifest = null;
const scene = new OfficeScene($('officeScene'), id => showAgent(id));
const companyWorkbench = new CompanyWorkbench({ api, request, dialog, refresh, toast, download, showView });

async function request(path, options = {}) {
  const response = await fetch(path, { credentials: 'same-origin', ...options,
    headers: { ...(options.body && !(options.body instanceof FormData) ? { 'Content-Type': 'application/json' } : {}), ...options.headers } });
  if (!response.ok) {
    const body = await response.json().catch(() => ({}));
    throw new Error((body.error?.code ? `${body.error.code}: ` : '') + (body.error?.message || (typeof body.error === 'string' ? body.error : `Thao tác thất bại (${response.status}).`)));
  }
  return response;
}
async function api(path, options) { return (await request(path, options)).json(); }
const base = () => `/api/v1/companies/${companyId}`;
const post = (path, payload, key) => api(path, { method: 'POST', headers: key ? { 'Idempotency-Key': key } : {}, body: JSON.stringify(payload) });
function toast(message) { $('toast').textContent = message; $('toast').hidden = false; clearTimeout(toast.timer); toast.timer = setTimeout(() => $('toast').hidden = true, 5500); }
function bindForm(id, action) {
  $(id).onsubmit = async e => {
    e.preventDefault(); const button = e.currentTarget.querySelector('button[type=submit],button:not([type])');
    if (button?.disabled) return;
    if (button) button.disabled = true;
    try { await action(); } catch (error) { toast(error.message); }
    finally { if (button) button.disabled = false; }
  };
}
function click(id, action) { $(id).onclick = () => Promise.resolve().then(action).catch(error => toast(error.message)); }
function dialog(title, content) { $('dialogTitle').textContent = title; $('dialogBody').innerHTML = content; if (!$('detailDialog').open) $('detailDialog').showModal(); }
click('closeDialog', () => $('detailDialog').close());
function showView(next) {
  view = next;
  document.querySelectorAll('.view').forEach(node => node.hidden = node.id !== `view-${next}`);
  document.querySelectorAll('[data-view]').forEach(button => {
    button.classList.toggle('active', button.dataset.view === next);
    if (button.dataset.view === next) button.setAttribute('aria-current', 'page'); else button.removeAttribute('aria-current');
  });
  [$('pageTitle').textContent, $('pageDescription').textContent] = titles[next];
  scene.setVisible(next === 'office' && !$('boardOnly').checked);
  companyWorkbench.setVisible(next === 'company');
}
document.querySelectorAll('[data-view]').forEach(button => button.onclick = () => showView(button.dataset.view));
function roleChecks(chosen = []) { return roles.map(role => `<label><input type="checkbox" name="roles" value="${escape(role.roleId)}" ${chosen.includes(role.roleId) ? 'checked' : ''}> ${escape(role.roleId.toUpperCase())}</label>`).join(''); }
function chosenRoles(form) { return [...form.querySelectorAll('input[name=roles]:checked')].map(node => node.value); }

async function refresh() {
  if (!companyId) return;
  const version = ++refreshVersion, company = companyId, path = base();
  const snapshot = await api(`${path}/workspace/snapshot`);
  const { runs, agents, events, sources, canonical, proposals, office, workbench } = snapshot;
  const health = { storage: snapshot.storage };
  if (version !== refreshVersion || company !== companyId) return;
  state = { health, runs, agents, events, sources, canonical, proposals, office, workbench };
  cursor = Math.max(cursor, events.at(-1)?.seq ?? 0);
  if (!runs.some(run => run.runId === selectedRunId)) selectedRunId = runs[0]?.runId ?? null;
  render();
}
function scheduleRefresh() { clearTimeout(refreshTimer); refreshTimer = setTimeout(() => refresh().catch(error => toast(error.message)), 140); }
function render() {
  companyWorkbench.update(companyId, companies, state);
  const { runs = [], agents = [], events = [], sources = [], canonical = [], proposals = [] } = state;
  $('storageBadge').textContent = `Storage: ${state.health?.storage === 'sqlite' ? 'SQLite' : 'File Store'}`;
  $('statRuns').textContent = runs.length;
  $('statApprovals').textContent = runs.filter(run => ['awaiting_approval', 'awaiting_delivery_approval'].includes(run.state)).length;
  $('statAgents').textContent = agents.length; $('statMemory').textContent = canonical.length;
  $('runCount').textContent = `${runs.length} runs`;
  $('runs').innerHTML = runs.map(run => `<button class="run-item ${run.runId === selectedRunId ? 'selected' : ''}" data-run="${escape(run.runId)}"><b>${escape(run.objective.slice(0, 100))}</b><span class="tag">${escape(labels[run.state] ?? run.state)}</span> <span class="small">#${run.lastSequence}</span></button>`).join('') || empty('Chưa có nhiệm vụ. Tạo yêu cầu đầu tiên ở trên.');
  document.querySelectorAll('[data-run]').forEach(button => button.onclick = () => { selectedRunId = button.dataset.run; render(); });
  $('eventCursor').textContent = `#${events.at(-1)?.seq ?? 0}`;
  $('events').innerHTML = [...events].reverse().slice(0, 80).map(e => `<div class="event" id="event-${e.seq}"><span class="event-seq">${e.seq}</span><div><b>${escape(eventNames[e.type] ?? e.type)}</b><p>${escape(e.runId)}<br>${escape(new Date(e.createdAt).toLocaleString('vi-VN'))}</p></div></div>`).join('') || empty('Sự kiện sẽ xuất hiện khi có thao tác.');
  renderRun(runs.find(run => run.runId === selectedRunId));
  renderDelivery(runs.find(run => run.runId === selectedRunId), { post, request, dialog, refresh, toast, download, agents, base: base() });
  for (const [selectId, role] of [['baAgent', 'ba'], ['leaderAgent', 'leader']]) {
    const current = $(selectId).value;
    $(selectId).innerHTML = agents.filter(agent => agent.roleIds.includes(role)).map(agent => `<option value="${escape(agent.agentId)}">${escape(agent.name)}</option>`).join('');
    if ([...$(selectId).options].some(option => option.value === current)) $(selectId).value = current;
  }
  const chosenSources = [...$('modelSources').selectedOptions].map(option => option.value);
  $('modelSources').innerHTML = sources.map(source => `<option value="${escape(source.sourceId)}" ${chosenSources.includes(source.sourceId) ? 'selected' : ''}>${escape(source.title)} · v${source.revision}</option>`).join('');
  $('sourceList').innerHTML = sources.map(source => `<div class="record"><h3>${escape(source.title)}</h3><span class="meta">v${source.revision} · ${source.characters} ký tự · ${escape(source.kind)} · ${escape(source.classification)}</span><p class="hash">${escape(source.contentSha256)}</p><button class="button" data-source="${escape(source.sourceId)}" data-revision="${source.revision}">Mở nguồn</button><button class="button" data-revise="${escape(source.sourceId)}">Thêm phiên bản</button></div>`).join('') || empty('Chưa có nguồn tài liệu.');
  document.querySelectorAll('[data-source]').forEach(button => button.onclick = () => showSource(button.dataset.source, Number(button.dataset.revision)).catch(error => toast(error.message)));
  document.querySelectorAll('[data-revise]').forEach(button => button.onclick = () => reviseSource(button.dataset.revise).catch(error => toast(error.message)));
  $('canonical').innerHTML = canonical.map(record => `<div class="record"><h3>${escape(record.stableKey)} <span class="tag success">${escape(record.kind)} · v${record.version}</span></h3><p>${escape(record.body)}</p><span class="meta">Owner đã duyệt · ${record.citations.length} trích dẫn${record.citations.some(c => sources.find(s => s.sourceId === c.sourceId)?.revision !== c.revision) ? ' · Nguồn đã có bản mới, cần đối chiếu' : ''}</span></div>`).join('') || empty('Chưa có bộ nhớ chính thức.');
  $('proposals').innerHTML = proposals.map(p => `<div class="record"><h3>${escape(p.stableKey)} <span class="tag">${escape(p.status)}</span></h3><p>${escape(p.body)}</p><span class="meta">${escape(p.kind)} · ${p.citations.length} trích dẫn</span>${p.status === 'proposed' ? `<div class="row-actions"><button class="button approve" data-review="${escape(p.proposalId)}" data-accepted="true">Duyệt làm chính thức</button><button class="button" data-review="${escape(p.proposalId)}" data-accepted="false">Từ chối</button></div>` : ''}</div>`).join('') || empty('Chưa có đề xuất. Tìm một nguồn và chọn “Đề xuất bộ nhớ”.');
  document.querySelectorAll('[data-review]').forEach(button => button.onclick = async () => {
    const p = proposals.find(item => item.proposalId === button.dataset.review);
    button.disabled = true;
    try {
      await post(`${base()}/memory/proposals/${p.proposalId}/review`, { accepted: button.dataset.accepted === 'true', expectedCanonicalVersion: canonical.find(c => c.stableKey === p.stableKey)?.version ?? 0 });
      await refresh(); toast('Đã lưu quyết định duyệt bộ nhớ.');
    } catch (error) { toast(error.message); scheduleRefresh(); } finally { button.disabled = false; }
  });
  $('agentRegistry').innerHTML = agents.map(agent => `<div class="agent-record"><h3>${escape(agent.name)} <span class="tag">v${agent.version}</span></h3><p>${escape(agent.roleIds.join(' + '))}</p><span class="meta">Model: chọn theo run · Tool grants: ${agent.toolGrants.length}</span><div class="row-actions"><button class="button" data-edit-agent="${escape(agent.agentId)}">Gán vai trò</button></div></div>`).join('');
  document.querySelectorAll('[data-edit-agent]').forEach(button => button.onclick = () => editAgent(button.dataset.editAgent));
  const office = state.office ?? { agents: [], cursor: 0 };
  $('officeCursor').textContent = `Event #${office.cursor}`;
  $('officeAgents').innerHTML = office.agents.map(item => `<button class="agent-tile" data-office-agent="${escape(item.agent.agentId)}"><span class="tag ${item.state.includes('approval') ? 'waiting' : ''}">${escape(labels[item.state] ?? item.state)}</span><b>${escape(item.agent.name)}</b><small>${escape(item.agent.roleIds.join(' + '))} · #${item.sequence}</small></button>`).join('');
  document.querySelectorAll('[data-office-agent]').forEach(button => button.onclick = () => showAgent(button.dataset.officeAgent));
  scene.update(office);
}
function renderRun(run) {
  $('runState').textContent = run ? (labels[run.state] ?? run.state) : 'Chưa có run';
  $('runState').className = `tag ${run?.state === 'awaiting_approval' ? 'waiting' : ''}`;
  const isAI = run?.executionMode === "local_model";
  const stages = [
    ['01', 'BA / Spec', run?.spec ? (isAI ? 'Output AI đã lưu' : 'produced · mẫu local') : 'Chưa tạo', !!run?.spec],
    ['02', 'Leader / Plan', run?.plan ? (isAI ? 'Output AI đã lưu' : 'produced · mẫu local') : 'Chờ ACK', !!run?.plan],
    ['03', 'Owner', run?.ownerApproved === true ? 'Đã duyệt plan' : run?.ownerApproved === false ? 'Cần sửa lại' : 'Chưa quyết định', run?.ownerApproved === true],
    ['04', 'Dev / Files', run?.delivery?.artifact ? 'Gói file đã tạo' : 'Sau plan approval', !!run?.delivery?.artifact],
    ['05', 'Tester / SOC / QA', run?.delivery?.receipts?.length ? (run.delivery.receipts.every(r => r.exitCode === 0) ? 'Static checks PASS' : 'Bàn giao bị chặn') : 'Chưa chạy kiểm tra', run?.delivery?.receipts?.length === 3 && run.delivery.receipts.every(r => r.exitCode === 0)],
    ['06', 'Delivery', run?.state === 'delivered' ? 'Owner đã duyệt ZIP' : 'Chờ kiểm tra / Owner', run?.state === 'delivered']
  ];
  $('pipeline').innerHTML = stages.map(([n, name, text, done], i) => `<div class="stage ${done ? 'done' : ''} ${i > 2 && !run?.delivery ? 'future' : ''} ${i === 2 && run?.state === 'awaiting_approval' ? 'wait' : ''}"><small>${n} / ${done ? 'RECORDED' : 'PENDING'}</small><b>${name}</b><span>${escape(text)}</span></div>`).join('');
  $('runActions').replaceChildren(); $('packet').replaceChildren(); $('invocations').replaceChildren();
  if (!run) { $('selectedRun').innerHTML = empty('Tạo một yêu cầu để khởi tạo spec và packet bàn giao.'); return; }
  $('selectedRun').innerHTML = `<h3 class="run-objective">${escape(run.objective)}</h3><div class="meta">${escape(run.runId)}<br>Owner bước hiện tại: ${escape(run.currentOwner)} · Checkpoint #${run.lastSequence}${run.resumeState ? `<br>Khi tiếp tục: ${escape(labels[run.resumeState] ?? run.resumeState)}` : ''}</div>`;
  const controls = [];
  if (run.state === 'awaiting_handoff') controls.push(['ack', 'ACK bàn giao cho Leader', 'primary']);
  if (run.state === 'awaiting_approval') controls.push(['approve', 'Duyệt kế hoạch', 'approve'], ['reject', 'Yêu cầu sửa lại', '']);
  if (['awaiting_handoff', 'awaiting_approval', 'queued', 'running', 'blocked', 'awaiting_delivery_input', 'delivery_blocked', 'delivery_revision_requested', 'awaiting_delivery_approval'].includes(run.state)) controls.push(['pause', 'Tạm dừng workflow', '']);
  if (run.state === 'paused') controls.push(['resume', 'Tiếp tục workflow', 'primary']);
  if (!['approved', 'revision_requested', 'cancelled', 'delivered'].includes(run.state)) controls.push(['cancel', 'Hủy run', 'danger']);
  if (isAI && run.state === 'blocked' && run.invocations?.some(job => ['failed', 'interrupted'].includes(job.status) && job.attempts < 2)) controls.push(['retry', 'Retry AI · còn 1 lượt', 'primary']);
  controls.push(['report', 'Xem báo cáo', '']);
  controls.forEach(([action, text, style]) => {
    const button = document.createElement('button'); button.className = `button ${style}`; button.id = `run-${action}`; button.textContent = text;
    button.onclick = async () => {
      button.disabled = true;
      try {
        const path = `${base()}/workflow-runs/${run.runId}`;
        if (action === 'report') { const report = await api(`${path}/report`); dialog('Báo cáo từ checkpoint', `<p>Artifacts đã lưu: ${report.producedArtifacts.length}. Execution receipts: ${report.evidenceRefs.length}.</p><p class="helper">${escape(report.limitations.join(' '))}</p><pre>${escape(JSON.stringify(report, null, 2))}</pre><button class="button" id="downloadReport">Tải báo cáo JSON</button>`); click('downloadReport', () => download(new Blob([JSON.stringify(report, null, 2)], { type: 'application/json' }), `${run.runId}-report.json`)); return; }
        if (action === 'ack') await post(`${path}/handoff/ack`, { packetHash: run.handoff.contentSha256, receiverAgentId: run.leaderAgentId ?? 'leader-agent', expectedSequence: run.lastSequence }, `ack-${run.handoff.handoffId}`);
        else if (action === 'approve' || action === 'reject') await post(`${path}/approval`, { approved: action === 'approve', expectedSequence: run.lastSequence }, `owner-${run.runId}-${action}`);
        else await post(`${path}/${action}`, { expectedSequence: run.lastSequence }, `control-${action}-${run.runId}-${run.lastSequence}`);
        await refresh(); toast('Đã lưu command và checkpoint.');
      } catch (error) { toast(error.message); scheduleRefresh(); } finally { button.disabled = false; }
    };
    $('runActions').append(button);
  });
  if (run.lastError) {
    const error = document.createElement('p'); error.className = 'helper'; error.textContent = `Lỗi: ${run.lastError}. Retry dùng cùng input; model và quyền không tự thay đổi.`;
    $('invocations').append(error);
  }
  for (const job of run.invocations ?? []) {
    const card = document.createElement('div'); card.className = 'record';
    card.innerHTML = `<h3>${escape(job.roleId.toUpperCase())} · ${escape(job.model.name)} <span class="tag">${escape(labels[job.status] ?? job.status)}</span></h3><p class="meta">Lần gọi ${job.attempts}/2 · Token input ${job.usage?.inputTokens ?? 'chưa có số liệu'} · output ${job.usage?.outputTokens ?? 'chưa có số liệu'}</p>${job.output ? `<p>${escape(job.output.summary)}</p><ul>${job.output.deliverables.map(item => `<li>${escape(item)}</li>`).join('')}</ul>${job.output.questions.length ? `<p class="helper">Câu hỏi: ${escape(job.output.questions.join(' · '))}</p>` : ''}` : ''}<p class="hash">Input ${escape(job.inputSha256)}${job.outputSha256 ? `<br>Output ${escape(job.outputSha256)}` : ''}</p>`;
    const inspect = document.createElement('button'); inspect.className = 'button'; inspect.textContent = 'Xem context đã chụp';
    inspect.onclick = () => dialog('Input của invocation', `<p class="helper">Context của role được chụp tại lúc xếp hàng. Kết quả AI chưa phải sự thật đã kiểm chứng.</p><pre>${escape(JSON.stringify({ model: job.model, context: job.context, inputSha256: job.inputSha256 }, null, 2))}</pre>`);
    card.append(inspect); $('invocations').append(card);
  }
  if (run.handoff) {
    $('packet').innerHTML = `<details class="packet"><summary>Handoff packet · ${escape(run.handoff.handoffId)}</summary><p class="hash">${escape(run.handoff.contentSha256)}</p><div class="meta">Snapshot: ${escape(run.handoff.contextSnapshotId)}<br>Ràng buộc đã chụp: ${run.spec?.canonicalRecords?.length ?? 0}</div><button class="button space-top" id="openContext">Kiểm tra context</button></details>`;
    click('openContext', async () => { const context = await api(`${base()}/context/${run.handoff.contextSnapshotId}`); dialog('Context bất biến của run', `<pre>${escape(JSON.stringify(context, null, 2))}</pre>`); });
  }
}
async function showSource(id, revision, citation = null) {
  const source = await api(`${base()}/sources/${id}/revisions/${revision}`);
  const pinned = citation && citation.contentSha256 === source.contentSha256 && citation.start >= 0 && citation.end <= source.content.length;
  const content = pinned ? `${escape(source.content.slice(0, citation.start))}<mark>${escape(source.content.slice(citation.start, citation.end))}</mark>${escape(source.content.slice(citation.end))}` : escape(source.content);
  dialog(`${source.title} · v${source.revision}`, `<p class="hash">${escape(source.contentSha256)}</p><p class="helper">${escape(source.kind)} · ${escape(source.classification)} · parser ${escape(source.parserVersion)}${source.originalName ? ` · ${escape(source.originalName)}` : ''}. Nguồn chưa được xác minh; citation dùng chỉ số UTF-16.</p><pre>${content}</pre>`);
  if (pinned) $('dialogBody').querySelector('mark')?.scrollIntoView({ block: 'center' });
}
async function reviseSource(id) {
  const metadata = state.sources.find(s => s.sourceId === id);
  const source = await api(`${base()}/sources/${id}/revisions/${metadata.revision}`);
  dialog('Thêm phiên bản nguồn', `<form id="revisionForm"><p>Giữ nguyên bản cũ và tạo v${source.revision + 1}.</p><label for="revisionText">${escape(source.title)}</label><textarea id="revisionText" maxlength="262144" required>${escape(source.content)}</textarea><button class="button primary" type="submit">Lưu phiên bản mới</button></form>`);
  bindForm('revisionForm', async () => { await post(`${base()}/sources`, { sourceId: id, title: source.title, content: $('revisionText').value, expectedRevision: source.revision, kind: source.kind, classification: source.classification, parserVersion: 'owner-edit-v1', originalName: source.originalName }); $('detailDialog').close(); await refresh(); toast('Đã lưu phiên bản nguồn mới.'); });
}
function editAgent(id) {
  const agent = state.agents.find(a => a.agentId === id);
  dialog(`Vai trò · ${agent.name}`, `<form id="roleForm"><fieldset><legend>Chọn một hoặc nhiều vai trò</legend><div class="role-options">${roleChecks(agent.roleIds)}</div></fieldset><p class="helper">Tool grants vẫn là ${agent.toolGrants.length}. Thay đổi áp dụng cho run mới; run cũ giữ snapshot đã chụp.</p><button class="button primary space-top" type="submit">Lưu vai trò</button></form>`);
  bindForm('roleForm', async () => { await api(`${base()}/agents/${id}/roles`, { method: 'PUT', body: JSON.stringify({ roleIds: chosenRoles($('roleForm')), expectedVersion: agent.version }) }); $('detailDialog').close(); await refresh(); });
}
function showAgent(id) {
  const item = state.office?.agents.find(a => a.agent.agentId === id); if (!item) return;
  dialog(item.agent.name, `<p><span class="tag">${escape(labels[item.state] ?? item.state)}</span></p><p>${escape(item.summary)}</p><dl class="meta"><dt>Vai trò</dt><dd>${escape(item.agent.roleIds.join(' + '))}</dd><dt>Model</dt><dd>${escape(item.modelName ?? 'Chọn theo từng run')}</dd><dt>Role context</dt><dd>${escape(item.activeRoleId ?? 'Không có')}</dd><dt>Tool grants</dt><dd>${item.agent.toolGrants.length}</dd><dt>Run</dt><dd>${escape(item.runId ?? 'Không có')}</dd><dt>Checkpoint</dt><dd>#${item.sequence}</dd></dl>${item.runId ? '<button class="button" id="openAgentRun">Mở nhiệm vụ</button>' : ''}`);
  if (item.runId) click('openAgentRun', () => { selectedRunId = item.runId; $('detailDialog').close(); showView('work'); render(); });
}
function propose(hit) {
  dialog('Đề xuất bộ nhớ có nguồn', `<form id="proposalForm"><label for="memoryKey">Mã ổn định (dùng lại mã để tạo phiên bản kế tiếp)</label><input id="memoryKey" required maxlength="100" value="${uid('decision')}"><label for="memoryKind">Loại</label><select id="memoryKind"><option value="constraint">Ràng buộc</option><option value="decision">Quyết định</option><option value="fact">Thông tin</option></select><label for="memoryBody">Nội dung cần duyệt</label><textarea id="memoryBody" maxlength="4000" required>${escape(hit.excerpt)}</textarea><p class="helper">Nguồn ${escape(hit.title)}, v${hit.citation.revision}, [${hit.citation.start}, ${hit.citation.end}). Đề xuất chưa phải bộ nhớ chính thức.</p><button class="button primary space-top" type="submit">Lưu đề xuất</button></form>`);
  const id = uid('proposal');
  bindForm('proposalForm', async () => { await post(`${base()}/memory/proposals`, { proposalId: id, stableKey: $('memoryKey').value, kind: $('memoryKind').value, body: $('memoryBody').value, citations: [hit.citation] }); $('detailDialog').close(); await refresh(); toast('Đề xuất đang chờ Owner duyệt.'); });
}

async function connect() {
  clearTimeout(retryTimer); stream?.close();
  const generation = ++streamGeneration, company = companyId;
  if (!company) return;
  const current = new EventSource(`${base()}/events/stream?afterSeq=${cursor}`); stream = current;
  current.onopen = () => { if (generation !== streamGeneration) return; $('connection').textContent = '● Live events'; $('connection').className = 'pill live'; };
  current.addEventListener('workspace', event => {
    if (generation !== streamGeneration || company !== companyId) return;
    const item = JSON.parse(event.data); if (item.seq <= cursor) return;
    cursor = item.seq; scheduleRefresh();
  });
  current.addEventListener('reset', () => { if (generation !== streamGeneration) return; cursor = 0; refresh().then(connect).catch(error => toast(error.message)); });
  current.onerror = () => {
    if (generation !== streamGeneration) return;
    current.close(); $('connection').textContent = 'Đang kết nối lại'; $('connection').className = 'pill disconnected';
    retryTimer = setTimeout(async () => {
      if (generation !== streamGeneration || company !== companyId) return;
      try { await api('/api/session'); await refresh(); if (generation === streamGeneration) await connect(); }
      catch { current.onerror(); }
    }, 2000);
  };
}
async function loadCompanies() {
  companies = await api('/api/v1/companies');
  $('companySelect').innerHTML = companies.map(c => `<option value="${escape(c.companyId)}">${escape(c.name)}</option>`).join('');
}
async function selectCompany(id) {
  ++streamGeneration; stream?.close(); clearTimeout(retryTimer); ++refreshVersion;
  companyId = id; $('companySelect').value = id; selectedRunId = null; cursor = 0; state = {};
  $('detailDialog').close(); scene.clear(); render();
  for (const input of ['objective', 'sources', 'sourceTitle', 'sourceText', 'searchQuery']) $(input).value = '';
  $('searchResults').replaceChildren(); $('sourceImportFile').value = ''; $('executionMode').value = 'scaffold'; updateModelMode(); createKey = uid('create'); sourceId = uid('source'); agentId = uid('agent');
  await refresh(); await connect();
}
$('companySelect').onchange = () => selectCompany($('companySelect').value).catch(error => toast(error.message));
click('addCompany', () => {
  dialog('Tạo công ty mới', '<form id="companyForm"><label for="companyName">Tên hiển thị</label><input id="companyName" maxlength="120" required placeholder="Game Development Studio"><label for="companyId">Mã công ty (chữ, số, dấu - hoặc _)</label><input id="companyId" maxlength="100" pattern="[A-Za-z0-9_\\-]+" required placeholder="game-studio"><button class="button primary" type="submit">Tạo công ty local</button><p class="helper">Mỗi công ty có agent và kho kiến thức riêng. Strict Local là mặc định.</p></form>');
  bindForm('companyForm', async () => { const result = await post('/api/v1/companies', { companyId: $('companyId').value, name: $('companyName').value }); $('detailDialog').close(); await loadCompanies(); await selectCompany(result.companyId); toast('Đã tạo công ty mới.'); });
});
bindForm('createRun', async () => {
  const mode = $('executionMode').value;
  const sourceRefs = mode === 'local_model' ? [...$('modelSources').selectedOptions].map(o => o.value) : $('sources').value.split(',').map(s => s.trim()).filter(Boolean);
  const run = await post(`${base()}/workflow-runs`, { objective: $('objective').value.trim(), sourceRefs, baAgentId: $('baAgent').value, leaderAgentId: $('leaderAgent').value, executionMode: mode, modelId: mode === 'local_model' ? $('localModel').value : null }, createKey);
  selectedRunId = run.runId; createKey = uid('create'); $('objective').value = ''; await refresh(); toast(run.executionMode === 'local_model' ? 'Đã xếp hàng AI local với context đã chụp.' : 'Spec mẫu và handoff packet đã được lưu.');
});
for (const id of ['objective', 'sources', 'baAgent', 'leaderAgent', 'executionMode', 'localModel', 'modelSources']) $(id).addEventListener('input', () => createKey = uid('create'));
bindForm('importSource', async () => {
  const file = $('sourceImportFile').files[0], classification = $('sourceClassification').value;
  if (file) {
    const form = new FormData(); form.set('sourceId', sourceId); form.set('title', $('sourceTitle').value); form.set('expectedRevision', '0'); form.set('classification', classification); form.set('file', file);
    await api(`${base()}/sources/file`, { method: 'POST', body: form });
  } else {
    if (!$('sourceText').value.trim()) throw new Error('Chọn một file hoặc nhập nội dung văn bản.');
    await post(`${base()}/sources`, { sourceId, title: $('sourceTitle').value, content: $('sourceText').value, expectedRevision: 0, kind: 'text', classification, parserVersion: 'owner-text-v1' });
  }
  sourceId = uid('source'); $('sourceTitle').value = ''; $('sourceText').value = ''; $('sourceImportFile').value = ''; await refresh(); toast('Đã parse và lưu nguồn. Nội dung chưa được duyệt làm bộ nhớ.');
});
bindForm('searchForm', async () => {
  const company = companyId, result = await api(`${base()}/knowledge/search?q=${encodeURIComponent($('searchQuery').value)}`);
  if (company !== companyId) return;
  $('knowledgeIndexStatus').textContent = `${result.mode} · ${result.indexVersion} · ${result.warnings[0]}`;
  $('searchResults').innerHTML = result.hits.map((hit, i) => `<div class="result"><h3>${escape(hit.title)} <span class="tag">v${hit.citation.revision}</span> <span class="tag">${escape(hit.classification)}</span></h3><pre>${escape(hit.excerpt)}</pre><p class="meta">Score ${Number(hit.score).toFixed(3)} · lexical ${Number(hit.lexicalScore).toFixed(3)} · vector ${Number(hit.vectorScore).toFixed(3)} · ${escape(hit.canonicalStatus)}<br>${escape(hit.citation.sourceId)} · UTF-16 [${hit.citation.start}, ${hit.citation.end})</p><button class="button" data-inspect-hit="${i}">Xem nguồn / highlight</button><button class="button" data-propose="${i}">Đề xuất bộ nhớ</button></div>`).join('') || empty('Không tìm thấy nguồn phù hợp.');
  document.querySelectorAll('[data-inspect-hit]').forEach(button => button.onclick = () => { const citation = result.hits[Number(button.dataset.inspectHit)].citation; showSource(citation.sourceId, citation.revision, citation).catch(error => toast(error.message)); });
  document.querySelectorAll('[data-propose]').forEach(button => button.onclick = () => propose(result.hits[Number(button.dataset.propose)]));
});
bindForm('createAgent', async () => { await post(`${base()}/agents`, { agentId, name: $('agentName').value, roleIds: chosenRoles($('createAgent')) }); agentId = uid('agent'); $('agentName').value = ''; await refresh(); toast('Đã tạo agent. Chưa cấp quyền công cụ.'); });
click('pauseVisual', () => { const paused = !scene.paused; scene.setPaused(paused); $('pauseVisual').textContent = paused ? 'Tiếp tục hoạt ảnh' : 'Dừng hoạt ảnh'; $('pauseVisual').setAttribute('aria-pressed', String(paused)); });
$('boardOnly').onchange = () => { $('officeScene').toggleAttribute('hidden', $('boardOnly').checked); scene.setVisible(view === 'office' && !$('boardOnly').checked); };
document.addEventListener('keydown', e => { if (e.code === 'Space' && view === 'office' && e.target === document.body && !$('detailDialog').open) { e.preventDefault(); $('pauseVisual').click(); } });
click('refresh', refresh);
async function verifyStorage() {
  manifest = await api('/api/v1/storage/verify');
  $('integrityResult').innerHTML = `<span class="tag success">Đã đối chiếu ${manifest.companies.reduce((n, c) => n + c.eventCount, 0)} events</span><p class="hash">${escape(manifest.digest)}</p><div class="table-scroll"><table class="data-table"><thead><tr><th>Kho</th><th>Events</th><th>Chưa có hash</th></tr></thead><tbody>${manifest.companies.map(c => `<tr><td>${escape(c.companyId === '__profile' ? 'Danh mục Owner' : c.companyId)}</td><td>${c.eventCount}</td><td>${c.legacyEvents}</td></tr>`).join('')}</tbody></table></div>`;
  $('targetBackend').value = manifest.backend === 'file' ? 'sqlite' : 'file';
}
click('verifyStorage', verifyStorage);
bindForm('migrateStorage', async () => {
  const backend = $('targetBackend').value;
  if (!manifest) await verifyStorage();
  const result = await post('/api/v1/storage/migrate', { backend, expectedDigest: manifest.digest });
  await verifyStorage(); await refresh(); toast(result.changed ? 'Đã chuyển storage; số event và checksum khớp.' : 'Storage đã dùng backend này.');
});
function download(blob, name) { const url = URL.createObjectURL(blob), anchor = document.createElement('a'); anchor.href = url; anchor.download = name; anchor.click(); setTimeout(() => URL.revokeObjectURL(url), 1000); }
bindForm('backupForm', async () => {
  const passphrase = $('backupPassphrase').value; $('backupPassphrase').value = '';
  const response = await request('/api/v1/backups/export', { method: 'POST', body: JSON.stringify({ passphrase }) });
  download(await response.blob(), `ai-company-${new Date().toISOString().slice(0, 10)}.aicos`); toast('Đã tạo bản sao lưu mã hóa. Hãy giữ mật khẩu riêng.');
});
bindForm('validateBackup', async () => {
  const file = $('backupFile').files[0], passphrase = $('validatePassphrase').value; $('validatePassphrase').value = '';
  if (file.size > 128 * 1024 * 1024) throw new Error('Bản sao lưu vượt giới hạn 128 MiB.');
  const encoded = await new Promise((resolve, reject) => { const reader = new FileReader(); reader.onload = () => resolve(reader.result.split(',')[1]); reader.onerror = reject; reader.readAsDataURL(file); });
  const result = await post('/api/v1/backups/validate', { archiveBase64: encoded, passphrase });
  $('backupResult').innerHTML = `<span class="tag success">Giải mã và checksum hợp lệ · ${result.eventCount} events</span><p class="hash">${escape(result.digest)}</p>`;
});
async function start() {
  await api('/api/session'); roles = await api('/api/v1/roles');
  $('newAgentRoles').innerHTML = roleChecks(['ba']);
  $('roleCatalog').innerHTML = roles.map(role => `<article class="role-card"><h3>${escape(role.name)} <span class="tag">${escape(role.roleId)}</span></h3><p>Input: ${escape(role.inputSchema)}</p><p>Output: ${escape(role.outputSchema)}</p><p>Skills: ${escape(role.skillIds.join(', '))}</p><p>Không được: ${escape(role.forbiddenActions.join(', '))}</p></article>`).join('');
  await loadCompanies(); await selectCompany(companies[0].companyId); showView('company');
}
start().catch(error => { $('connection').textContent = 'Không kết nối được'; $('connection').className = 'pill disconnected'; toast(error.message); });

function updateModelMode() {
  const enabled = $('executionMode').value === 'local_model';
  $('localModelControls').hidden = !enabled; $('scaffoldSources').hidden = enabled;
  $('localModel').required = enabled;
}
$('executionMode').addEventListener('change', updateModelMode);
click('loadModels', async () => {
  const button = $('loadModels'); button.disabled = true;
  try {
    const models = await api('/api/v1/providers/local/models');
    $('localModel').innerHTML = models.map(model => `<option value="${escape(model.name)}">${escape(model.name)} · ${escape(model.parameterSize)}</option>`).join('') || '<option value="">Không có model local phù hợp</option>';
    $('modelStatus').textContent = `${models.length} model GGUF local. Model remote/cloud không được đưa vào danh sách. Context 16k token; output BA/Leader 2k, Dev 4k token.`;
    createKey = uid('create');
  } finally { button.disabled = false; }
});
$('sourceImportFile').addEventListener('change', async () => {
  const file = $('sourceImportFile').files[0]; if (!file) return;
  try {
    if (file.size > 8 * 1024 * 1024) throw new Error('File vượt giới hạn 8 MiB.');
    $('sourceTitle').value = file.name.replace(/\.[^.]+$/, '').slice(0, 160);
    if (/\.(txt|md|markdown|json|csv|tsv|yaml|yml)$/i.test(file.name) && file.size <= 262144) $('sourceText').value = new TextDecoder('utf-8', { fatal: true }).decode(await file.arrayBuffer());
  } catch (error) { $('sourceImportFile').value = ''; toast(error.message); }
});
$('sourceText').addEventListener('input', () => { $('sourceImportFile').value = ''; });
