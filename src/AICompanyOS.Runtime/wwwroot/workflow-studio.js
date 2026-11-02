import { escapeHtml as esc } from './presentation.js';

const $ = id => document.getElementById(id);
export const workflowNames = { 'software-delivery': 'Phần mềm · song song', 'software-reviewed': 'Phần mềm · kiểm tra tuần tự', 'software-focused': 'Phần mềm · tập trung' };
const titles = { intake: 'Nhận yêu cầu', ba: 'BA · Yêu cầu', leader: 'Leader · Kế hoạch', 'approval-plan': 'Owner · Duyệt phạm vi', dev: 'Dev · Triển khai', build: 'Build · Container', tester: 'Tester · Kiểm thử', soc: 'SOC · An toàn', qa: 'QA · Nghiệm thu', gate: 'Cổng chất lượng', subpm: 'SubPM · Báo cáo', 'final-approval': 'Owner · Duyệt kết quả', release: 'Git · Bàn giao' };
const kinds = { intake: 'Đầu vào', agent: 'Nhân viên AI / nhập tay', approval: 'Bạn quyết định', fanout: 'Task song song', build: 'Build thật', test: 'Test thật', security: 'Kiểm tra bảo mật', quality: 'Đối chiếu tiêu chí', gate: 'Điều kiện đạt / vòng sửa', report: 'Tổng hợp bằng chứng', release: 'Commit / xuất source' };
const defaults = { intake: [20, 25], ba: [230, 25], leader: [440, 25], 'approval-plan': [650, 25], dev: [230, 150], build: [440, 150], tester: [20, 275], soc: [230, 275], qa: [440, 275], gate: [230, 400], subpm: [440, 400], 'final-approval': [650, 400], release: [650, 525] };
const clone = value => structuredClone(value);
const nextVersion = value => /^\d+\.\d+\.\d+$/.test(value) ? value.replace(/\d+$/, n => Number(n) + 1) : '1.0.1';
const goodShape = value => value && typeof value.name === 'string' && typeof value.version === 'string' && Array.isArray(value.nodes) && value.nodes.length > 0 && value.nodes.length <= 32 && value.nodes.every(n => n && typeof n.id === 'string' && typeof n.kind === 'string' && Array.isArray(n.dependsOn) && n.dependsOn.every(id => typeof id === 'string'));

export class WorkflowStudio {
  constructor(services) { this.s = services; }
  open({ companyId, base, definitions, templates, required }) {
    this.companyId = companyId; this.base = base; this.definitions = definitions; this.required = required;
    this.templates = [...new Map([...templates, ...definitions].map(w => [w.name + '@' + w.version, w])).values()];
    this.history = []; this.future = []; this.selected = 'dev'; this.connection = null; this.draft = clone(templates[0] ?? definitions.at(-1)); this.layout = clone(defaults);
    this.bumpVersion(); this.restoreLayout();
    this.s.dialog('Workflow Studio', `<form id="softwareWorkflowForm" class="workflow-designer"><div class="studio-topline"><p>Chọn mẫu, điều chỉnh giới hạn và phụ thuộc. Lưu thành phiên bản mới để dùng cho lần giao việc tiếp theo.</p><span class="tag">13 bước thực thi</span></div><div class="studio-template-row"><label for="studioTemplate">Mẫu / phiên bản đã lưu</label><select id="studioTemplate">${this.templates.map((w, i) => `<option value="${i}">${esc(workflowNames[w.name] ?? w.name)} · ${esc(w.version)}</option>`).join('')}</select><button type="button" class="button" id="studioApplyTemplate">Dùng mẫu này</button><button type="button" class="button" id="studioUndo" aria-label="Hoàn tác">↶</button><button type="button" class="button" id="studioRedo" aria-label="Làm lại">↷</button></div><div class="studio-config"><div><label for="studioName">Tên workflow</label><input id="studioName" required pattern="(?:[A-Za-z0-9_]|-)+" maxlength="100"></div><div><label for="studioVersion">Phiên bản mới</label><input id="studioVersion" required maxlength="32"></div><div><label for="studioParallel">Công việc đồng thời</label><input id="studioParallel" type="number" min="1" max="4" required></div><div><label for="studioAttempts">Số lần thử / job</label><input id="studioAttempts" type="number" min="1" max="3" required></div><div><label for="studioMinutes">Giới hạn phút</label><input id="studioMinutes" type="number" min="1" max="180" required></div><div><label for="studioTokens">Ngân sách output token</label><input id="studioTokens" type="number" min="1000" max="200000" required></div></div><div class="studio-editor"><div class="studio-graph-panel"><div class="studio-graph-tools"><span>Kéo để sắp xếp · Cổng ra → cổng vào để nối</span><button type="button" class="button" id="studioArrange">Sắp xếp lại</button><label class="sr-only" for="studioZoom">Độ phóng to</label><select id="studioZoom"><option value="60">60% · Toàn bộ</option><option value="75">75%</option><option value="100" selected>100%</option><option value="125">125%</option><option value="150">150%</option></select></div><div class="studio-canvas-scroll"><svg id="studioCanvas" viewBox="0 0 860 640" role="group" aria-label="Workflow có thể chỉnh sửa bằng kéo thả hoặc bàn phím"></svg></div><p class="helper">Các cổng Owner và kiểm tra bắt buộc được giữ. Liên kết thêm có thể bỏ; chu trình phụ thuộc sẽ bị từ chối.</p></div><aside class="studio-node-panel" id="studioNodeProperties"></aside></div><div id="workflowValidationResult" class="studio-validation" role="status">Chưa kiểm tra phiên bản đang sửa.</div><details class="technical-details"><summary>JSON nâng cao / nhập và xuất</summary><label for="workflowDefinitionJson">Definition đầy đủ</label><textarea id="workflowDefinitionJson" class="artifact-editor" spellcheck="false" maxlength="96000"></textarea><div class="row-actions"><button type="button" class="button" id="studioApplyJson">Áp dụng JSON</button><button type="button" class="button" id="studioExport">Xuất JSON</button><label class="button studio-import-label">Nhập JSON<input id="studioImportFile" type="file" accept=".json,application/json"></label></div></details><div class="studio-footer"><span id="studioDirty" class="helper">Bản mới chưa lưu</span><button type="button" class="button" id="dryRunSoftwareDefinition">Kiểm tra workflow</button><button type="submit" class="button primary" id="studioSave">Lưu phiên bản mới</button></div></form>`);
    $('detailDialog').classList.add('wide-dialog');
    for (const id of ['studioName', 'studioVersion', 'studioParallel', 'studioAttempts', 'studioMinutes', 'studioTokens']) $(id).onchange = () => { this.remember(); this.readConfig(); this.changed(); };
    $('studioApplyTemplate').onclick = () => { this.remember(); this.draft = clone(this.templates[Number($('studioTemplate').value)]); this.bumpVersion(); this.layout = clone(defaults); this.selected = 'dev'; this.changed(); this.render(); };
    $('studioUndo').onclick = () => this.travel(this.history, this.future);
    $('studioRedo').onclick = () => this.travel(this.future, this.history);
    $('studioArrange').onclick = () => { this.remember(); this.layout = clone(defaults); this.persistLayout(); this.renderGraph(); };
    $('studioZoom').onchange = () => $('studioCanvas').style.width = $('studioZoom').value + '%';
    $('dryRunSoftwareDefinition').onclick = () => this.validate().catch(e => this.showError(e.message));
    $('studioApplyJson').onclick = () => this.importDefinition($('workflowDefinitionJson').value).catch(e => this.showError(e.message));
    $('studioImportFile').onchange = async () => { const file = $('studioImportFile').files[0]; try { if (!file || file.size > 96000) throw new Error('Chọn file JSON tối đa 96 KB.'); await this.importDefinition(await file.text()); } catch (e) { this.showError(e.message); } finally { $('studioImportFile').value = ''; } };
    $('studioExport').onclick = () => { this.readConfig(); this.s.download(new Blob([JSON.stringify(this.draft, null, 2)], { type: 'application/json' }), 'company-workflow.json'); };
    $('softwareWorkflowForm').onsubmit = async e => {
      e.preventDefault(); const button = $('studioSave'); button.disabled = true;
      try { if (!(await this.validate()).valid) return; const result = await this.s.api(this.base + '/workflows', { method: 'POST', body: JSON.stringify(this.draft) }); if (!result.valid) { this.displayValidation(result); return; } this.persistLayout(); await this.s.refresh(); $('detailDialog').close(); this.s.toast('Đã lưu workflow mới. Run đang chạy giữ phiên bản đã chụp.'); }
      catch (error) { this.showError(error.message); } finally { button.disabled = false; }
    };
    this.bindCanvas(); this.render();
  }
  bumpVersion() { do { this.draft.version = nextVersion(this.draft.version); } while (this.definitions.some(w => w.name === this.draft.name && w.version === this.draft.version)); }
  storageKey() { return 'aicos.workflow-layout.' + this.companyId + '.' + this.draft.name; }
  restoreLayout() {
    try { const saved = JSON.parse(localStorage.getItem(this.storageKey())); if (saved && typeof saved === 'object') for (const [id, point] of Object.entries(saved)) if (Array.isArray(point) && point.length === 2 && point.every(Number.isFinite)) this.layout[id] = [Math.max(0, Math.min(670, point[0])), Math.max(0, Math.min(550, point[1]))]; } catch { /* Layout is optional. */ }
  }
  persistLayout() { try { localStorage.setItem(this.storageKey(), JSON.stringify(this.layout)); } catch { /* Execution definitions are stored by the server. */ } }
  snapshot() { return { draft: clone(this.draft), layout: clone(this.layout), selected: this.selected }; }
  remember() { this.history.push(this.snapshot()); if (this.history.length > 30) this.history.shift(); this.future = []; }
  travel(from, to) { if (!from.length) return; to.push(this.snapshot()); Object.assign(this, from.pop()); this.changed(); this.render(); }
  readConfig() { this.draft.name = $('studioName').value.trim(); this.draft.version = $('studioVersion').value.trim(); this.draft.maxParallel = Number($('studioParallel').value); this.draft.maxAttempts = Number($('studioAttempts').value); this.draft.maxElapsedMinutes = Number($('studioMinutes').value); this.draft.maxOutputTokens = Number($('studioTokens').value); }
  changed() { $('workflowValidationResult').className = 'studio-validation'; $('workflowValidationResult').textContent = 'Có thay đổi; cần kiểm tra lại trước khi lưu.'; $('studioDirty').textContent = 'Bản mới chưa lưu'; $('workflowDefinitionJson').value = JSON.stringify(this.draft, null, 2); this.updateHistory(); }
  updateHistory() { $('studioUndo').disabled = !this.history.length; $('studioRedo').disabled = !this.future.length; }
  render() {
    const d = this.draft;
    for (const [id, value] of Object.entries({ studioName: d.name, studioVersion: d.version, studioParallel: d.maxParallel, studioAttempts: d.maxAttempts, studioMinutes: d.maxElapsedMinutes, studioTokens: d.maxOutputTokens })) $(id).value = value;
    $('workflowDefinitionJson').value = JSON.stringify(d, null, 2); this.updateHistory(); this.renderGraph(); this.renderProperties();
  }
  renderGraph() {
    const canvas = $('studioCanvas');
    canvas.innerHTML = '<defs><pattern id="studioDots" width="20" height="20" patternUnits="userSpaceOnUse"><circle cx="2" cy="2" r="1" fill="#dce2d5"/></pattern><marker id="studioArrow" viewBox="0 0 10 10" refX="8" refY="5" markerWidth="6" markerHeight="6" orient="auto"><path d="M0 0L10 5L0 10Z" fill="#8da39b"/></marker></defs><rect width="860" height="640" fill="url(#studioDots)"/><g id="studioEdges"></g><g id="studioNodes"></g>';
    $('studioNodes').innerHTML = this.draft.nodes.map((node, i) => { const [x, y] = this.layout[node.id] ?? [20 + i % 4 * 210, 25 + Math.floor(i / 4) * 125]; return `<g transform="translate(${x} ${y})" class="studio-node ${this.selected === node.id ? 'selected' : ''} ${node.kind === 'approval' ? 'owner-node' : ''}" data-studio-node="${esc(node.id)}" role="button" tabindex="0" aria-label="${esc(titles[node.id] ?? node.id)}. Dùng phím mũi tên để di chuyển."><rect width="178" height="72" rx="10"/><text x="14" y="26" class="studio-node-title">${esc(titles[node.id] ?? node.id)}</text><text x="14" y="47" class="studio-node-kind">${esc(kinds[node.kind] ?? node.kind)}</text><circle cx="0" cy="36" r="6" class="studio-port in" data-studio-port="in" role="button" tabindex="0" aria-label="Nối vào ${esc(titles[node.id] ?? node.id)}"/><circle cx="178" cy="36" r="6" class="studio-port out ${this.connection === node.id ? 'connecting' : ''}" data-studio-port="out" role="button" tabindex="0" aria-label="Nối từ ${esc(titles[node.id] ?? node.id)}"/></g>`; }).join('');
    this.drawEdges();
  }
  drawEdges() {
    $('studioEdges').innerHTML = this.draft.nodes.flatMap(node => node.dependsOn.map(from => {
      const a = this.layout[from] ?? defaults[from], b = this.layout[node.id] ?? defaults[node.id]; if (!a || !b) return '';
      const required = this.required.nodes.find(n => n.id === node.id)?.dependsOn.includes(from);
      const ax = a[0] + 178, ay = a[1] + 36, bx = b[0], by = b[1] + 36;
      let path;
      if (Math.abs(ay - by) < 12 && bx - ax >= 0 && bx - ax < 80) path = `M${ax} ${ay}H${bx}`;
      else if (Math.abs(ay - by) < 12) { const lane = Math.max(8, Math.min(a[1], b[1]) - 16); path = `M${ax} ${ay}H${ax + 12}V${lane}H${bx - 12}V${by}H${bx}`; }
      else if (bx > ax) { const lane = (ax + bx) / 2; path = `M${ax} ${ay}H${lane}V${by}H${bx}`; }
      else { const lane = b[1] > a[1] ? (a[1] + 72 + b[1]) / 2 : (b[1] + 72 + a[1]) / 2; path = `M${ax} ${ay}H${ax + 12}V${lane}H${bx - 12}V${by}H${bx}`; }
      return `<path class="studio-edge ${required ? '' : 'added-edge'}" d="${path}" marker-end="url(#studioArrow)"/>`;
    })).join('');
  }
  renderProperties() {
    const node = this.draft.nodes.find(n => n.id === this.selected) ?? this.draft.nodes[0]; this.selected = node.id;
    const mandatory = this.required.nodes.find(n => n.id === node.id)?.dependsOn ?? [];
    $('studioNodeProperties').innerHTML = `<span class="eyebrow">BƯỚC ĐANG CHỌN</span><h3>${esc(titles[node.id] ?? node.id)}</h3><p>${esc(kinds[node.kind] ?? node.kind)}${node.role ? ' · ' + esc(node.role.toUpperCase()) : ''}</p><fieldset class="studio-dependencies"><legend>Chờ các bước sau hoàn thành</legend>${this.draft.nodes.filter(n => n.id !== node.id).map(n => `<label class="check"><input type="checkbox" data-studio-dependency="${esc(n.id)}" ${node.dependsOn.includes(n.id) ? 'checked' : ''} ${mandatory.includes(n.id) ? 'disabled' : ''}><span>${esc(titles[n.id] ?? n.id)}${mandatory.includes(n.id) ? ' · bắt buộc' : ''}</span></label>`).join('')}</fieldset>${node.kind === 'gate' ? `<label for="studioLoops">Số vòng sửa tự động tối đa</label><select id="studioLoops"><option value="0">Không tự sửa</option><option value="1">1 vòng</option><option value="2">2 vòng</option></select>` : ''}<p class="helper">${node.kind === 'approval' ? 'Chỉ Owner được duyệt; nối thêm bước không bỏ qua cổng này.' : node.kind === 'report' ? 'Báo cáo chạy cả khi kiểm tra lỗi để giữ bằng chứng.' : node.kind === 'release' ? 'Chỉ release khi được yêu cầu và đã nghiệm thu.' : 'Vai trò và loại bước thuộc hợp đồng phần mềm đã hỗ trợ. Có thể thêm phụ thuộc nhưng không bỏ kiểm tra bắt buộc.'}</p>`;
    $('studioNodeProperties').querySelectorAll('[data-studio-dependency]').forEach(input => input.onchange = () => { this.remember(); node.dependsOn = input.checked ? [...new Set([...node.dependsOn, input.dataset.studioDependency])] : node.dependsOn.filter(id => id !== input.dataset.studioDependency); this.changed(); this.renderGraph(); });
    if ($('studioLoops')) { $('studioLoops').value = node.maxLoop ?? 0; $('studioLoops').onchange = () => { this.remember(); node.maxLoop = Number($('studioLoops').value); node.onFail = node.maxLoop ? 'dev' : null; this.changed(); }; }
  }
  select(id) { this.selected = id; $('studioCanvas').querySelectorAll('[data-studio-node]').forEach(n => n.classList.toggle('selected', n.dataset.studioNode === id)); this.renderProperties(); }
  connect(port, id) {
    if (port === 'out') { this.connection = id; this.renderGraph(); return; }
    if (!this.connection || this.connection === id) { this.connection = null; this.renderGraph(); return; }
    const target = this.draft.nodes.find(n => n.id === id); this.remember(); target.dependsOn = [...new Set([...target.dependsOn, this.connection])]; this.connection = null; this.selected = id; this.changed(); this.renderGraph(); this.renderProperties();
  }
  bindCanvas() {
    const canvas = $('studioCanvas');
    const point = event => { const p = canvas.createSVGPoint(); p.x = event.clientX; p.y = event.clientY; return p.matrixTransform(canvas.getScreenCTM().inverse()); };
    canvas.onpointerdown = e => {
      const group = e.target.closest('[data-studio-node]'); if (!group) return;
      if (e.target.dataset.studioPort) { e.preventDefault(); this.connect(e.target.dataset.studioPort, group.dataset.studioNode); return; }
      this.select(group.dataset.studioNode); const p = point(e); this.remember(); this.drag = { id: this.selected, start: p, origin: [...(this.layout[this.selected] ?? [20, 20])], pointer: e.pointerId }; canvas.setPointerCapture(e.pointerId); e.preventDefault();
    };
    canvas.onpointermove = e => { if (!this.drag || e.pointerId !== this.drag.pointer) return; const p = point(e), d = this.drag; this.layout[d.id] = [Math.max(0, Math.min(670, d.origin[0] + p.x - d.start.x)), Math.max(0, Math.min(550, d.origin[1] + p.y - d.start.y))]; const node = [...canvas.querySelectorAll('[data-studio-node]')].find(n => n.dataset.studioNode === d.id); node.setAttribute('transform', `translate(${this.layout[d.id].join(' ')})`); this.drawEdges(); };
    const endDrag = () => { if (this.drag) { this.drag = null; this.persistLayout(); this.updateHistory(); } }; canvas.onpointerup = endDrag; canvas.onpointercancel = endDrag;
    canvas.onkeydown = e => {
      const group = e.target.closest('[data-studio-node]'); if (!group) return; const id = group.dataset.studioNode;
      if (['Enter', ' '].includes(e.key)) { e.preventDefault(); if (e.target.dataset.studioPort) this.connect(e.target.dataset.studioPort, id); else this.select(id); return; }
      const delta = { ArrowLeft: [-1, 0], ArrowRight: [1, 0], ArrowUp: [0, -1], ArrowDown: [0, 1] }[e.key];
      if (delta) { e.preventDefault(); this.remember(); const old = this.layout[id] ?? [20, 20], step = e.shiftKey ? 30 : 10; this.layout[id] = [Math.max(0, Math.min(670, old[0] + delta[0] * step)), Math.max(0, Math.min(550, old[1] + delta[1] * step))]; group.setAttribute('transform', `translate(${this.layout[id].join(' ')})`); this.drawEdges(); this.persistLayout(); this.updateHistory(); }
    };
  }
  async importDefinition(text) {
    if (text.length > 96000) throw new Error('Definition quá lớn.'); const value = JSON.parse(text); if (!goodShape(value)) throw new Error('Cần workflow JSON với danh sách node và dependency hợp lệ.');
    const result = await this.s.api('/api/v1/workflows/validate', { method: 'POST', body: JSON.stringify(value) });
    if (!result.valid) { this.displayValidation(result); return; }
    this.remember(); this.draft = value; this.layout = clone(defaults); this.bumpVersion(); this.selected = 'dev'; this.changed(); this.render();
  }
  async validate() { this.readConfig(); const result = await this.s.api('/api/v1/workflows/validate', { method: 'POST', body: JSON.stringify(this.draft) }); this.displayValidation(result); return result; }
  displayValidation(result) { const root = $('workflowValidationResult'); root.className = 'studio-validation ' + (result.valid ? 'valid' : 'invalid'); root.innerHTML = result.valid ? `<b>Workflow hợp lệ.</b> ${result.topologicalOrder.length} bước · ${result.expandedNodeIds.length} bước sau khi mở rộng vòng sửa. Khi giao việc, model và môi trường vẫn được kiểm tra riêng.` : `<b>Chưa thể lưu workflow.</b><ul>${result.errors.map(e => `<li>${esc(e)}</li>`).join('')}</ul>`; }
  showError(message) { $('workflowValidationResult').className = 'studio-validation invalid'; $('workflowValidationResult').textContent = message; }
}
