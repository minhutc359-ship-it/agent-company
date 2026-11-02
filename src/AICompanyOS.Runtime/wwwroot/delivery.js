const escape = value => String(value ?? '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[c]);
const $ = id => document.getElementById(id);
const sample = () => ({ title: 'Tài liệu dự án', summary: 'Bản mẫu để Owner chỉnh sửa theo kế hoạch.', files: [{ path: 'index.html', content: '<html lang="vi"><head><meta charset="utf-8"/><meta name="viewport" content="width=device-width, initial-scale=1"/><title>Tài liệu dự án</title><style>body{font-family:system-ui;max-width:60rem;margin:3rem auto;padding:1.5rem;color:#173b35;background:#fffef8}h1{font-size:2.5rem}p{line-height:1.7}</style></head><body><main><h1>Tài liệu dự án</h1><p>Thay nội dung này bằng sản phẩm đáp ứng kế hoạch đã duyệt.</p></main></body></html>' }] });

export function renderDelivery(run, services) {
  const { post, request, dialog, refresh, toast, download, agents, base } = services;
  const root = $('deliveryPanel');
  root.hidden = !run || (!run.delivery && run.state !== 'approved');
  if (root.hidden) return;
  const session = run.delivery, path = `${base}/workflow-runs/${run.runId}/delivery`;
  const body = $('deliveryBody');
  // Forms live in the modal, so event-stream refreshes cannot erase an unsaved edit.
  body.innerHTML = `<p class="helper">1.0 · Website tĩnh / tài liệu HTML, CSS, TXT, Markdown, JSON. File chỉ được tạo trong gói bàn giao; kiểm tra không chạy shell hoặc JavaScript.</p>`;
  function button(id, title, action, primary = false) {
    const b = document.createElement('button'); b.id = id; b.className = `button ${primary ? 'primary' : ''}`; b.textContent = title;
    b.onclick = async () => { b.disabled = true; try { await action(); } catch (error) { toast(error.message); } finally { b.disabled = false; } };
    body.append(b);
  }
  function form(id, action) {
    $(id).onsubmit = async event => {
      event.preventDefault(); const b = $(id).querySelector('button[type="submit"]'); b.disabled = true;
      try { await action(); $('detailDialog').close(); await refresh(); toast('Đã lưu bước bàn giao và kết quả kiểm tra.'); }
      catch (error) { toast(error.message); } finally { b.disabled = false; }
    };
  }
  const terminal = ['delivered', 'cancelled', 'paused'].includes(run.state);
  if (!session) {
    button('prepare-delivery', 'Chuẩn bị sản phẩm bàn giao', () => {
      const roleOptions = ['dev', 'tester', 'soc', 'qa', 'subpm'].map(role => `<label for="delivery-${role}">${role.toUpperCase()}</label><select id="delivery-${role}">${agents.filter(a => a.roleIds.includes(role)).map(a => `<option value="${escape(a.agentId)}">${escape(a.name)}</option>`).join('')}</select>`).join('');
      dialog('Phạm vi tạo sản phẩm', `<form id="prepareDeliveryForm"><p class="helper">Duyệt plan chưa cho phép tạo file. Bước này chỉ cho phép tạo gói văn bản; không cấp quyền repository, shell hoặc mạng.</p><label for="deliveryMode">Nguồn file</label><select id="deliveryMode"><option value="manual">Owner nhập file · không gọi model</option>${run.executionMode === 'local_model' ? '<option value="local_model">Dev dùng model local đã chụp</option>' : ''}</select><label for="deliveryCriteria">Tiêu chí literal: mỗi dòng đường-dẫn | chuỗi bắt buộc</label><textarea id="deliveryCriteria" required maxlength="7000">index.html | Tài liệu dự án</textarea><p class="helper">Đây là kiểm tra chuỗi chính xác; Owner vẫn cần kiểm tra ý nghĩa và tính đầy đủ.</p>${roleOptions}<button class="button primary" type="submit">Cho phép tạo gói văn bản</button></form>`);
      const key = crypto.randomUUID();
      form('prepareDeliveryForm', () => post(`${path}/prepare`, { expectedSequence: run.lastSequence, mode: $('deliveryMode').value,
        criteria: $('deliveryCriteria').value.split('\n').filter(line => line.trim()).map((line, i) => { const at = line.indexOf('|'); if (at < 0) throw new Error('Mỗi tiêu chí cần dấu |.'); return { id: `criterion-${i + 1}`, path: line.slice(0, at).trim(), contains: line.slice(at + 1).trim() }; }),
        assignments: Object.fromEntries(['dev', 'tester', 'soc', 'qa', 'subpm'].map(role => [role, $(`delivery-${role}`).value])) }, key));
    }, true);
    return;
  }
  const artifact = session.artifact, receipts = session.receipts ?? [];
  const info = document.createElement('div'); info.className = 'delivery-evidence';
  info.innerHTML = `<p class="meta">${escape(session.workflowVersion)} · ${escape(session.mode)}${artifact ? ` · Bản ${artifact.version}/3 · ${escape(artifact.origin)}` : ''}</p><p class="helper">Owner criteria: ${session.criteria.map(c => `${escape(c.path)} → ${escape(c.contains)}`).join(' · ')}</p>${artifact ? `<h3>${escape(artifact.draft.title)}</h3><p>${escape(artifact.draft.summary)}</p><p class="hash">${escape(artifact.sha256)}</p><ul>${artifact.files.map(f => `<li>${escape(f.path)} <span class="hash">${escape(f.sha256)}</span></li>`).join('')}</ul>` : '<p>Chưa có file; các bộ kiểm tra chưa chạy.</p>'}${receipts.map(r => `<details class="packet"><summary>${escape(r.roleId.toUpperCase())} · ${r.exitCode === 0 ? 'PASS' : 'BLOCK'} · ${escape(r.tool)} ${escape(r.toolVersion)}</summary><p class="meta">${escape(r.agentId)}${r.selfReviewed ? ' · self-reviewed' : ''} · ${escape(r.receiptId)}</p><ul>${r.checks.map(c => `<li>${c.passed ? '✓' : '✕'} ${escape(c.check)} — ${escape(c.message)}</li>`).join('')}</ul><p class="hash">Input ${escape(r.inputSha256)}<br>Log ${escape(r.logSha256)}</p></details>`).join('')}${session.report ? `<p class="helper">SubPM ${escape(session.report.reporterAgentId)}: ${escape(session.report.status)}. ${escape(session.report.limitations.join(' '))}</p>` : ''}`;
  body.append(info);
  if (artifact?.version >= 3 && ['delivery_blocked', 'delivery_revision_requested'].includes(run.state)) {
    const exhausted = document.createElement('p'); exhausted.className = 'helper'; exhausted.textContent = 'Đã dùng hết 3 phiên bản artifact. Hãy tạo run mới để thay đổi phạm vi hoặc tiêu chí.'; body.append(exhausted);
  }
  if (!terminal && ['awaiting_delivery_input', 'delivery_blocked', 'delivery_revision_requested'].includes(run.state) && (artifact?.version ?? 0) < 3) {
    button('submit-delivery', artifact ? 'Sửa và gửi bản mới' : 'Nhập file sản phẩm', () => {
      dialog('File sản phẩm · JSON', `<form id="artifactForm"><label for="artifactJson">title, summary, files [{path, content}]</label><textarea id="artifactJson" class="artifact-editor" required spellcheck="false" maxlength="150000">${escape(JSON.stringify(artifact?.draft ?? sample(), null, 2))}</textarea><p class="helper">Tối đa 8 file, 48 KiB UTF-8; phải có index.html dưới dạng XHTML đóng thẻ đầy đủ. Không nhận script, form, ảnh hoặc tài nguyên ngoài.</p><button class="button primary" type="submit">Lưu file và chạy kiểm tra</button></form>`);
      const key = crypto.randomUUID();
      form('artifactForm', () => post(`${path}/artifacts`, { expectedSequence: run.lastSequence, draft: JSON.parse($('artifactJson').value) }, key));
    }, true);
  }
  if (!terminal && artifact && receipts.length === 3 && receipts.every(r => r.exitCode === 0)) {
    button('preview-delivery', 'Xem trước trong khung cách ly', () => {
      dialog('Xem trước sản phẩm', '<p class="helper">Khung cách ly không chạy script hoặc tải tài nguyên mạng.</p><iframe id="deliveryPreview" class="delivery-preview" sandbox title="Sản phẩm tĩnh"></iframe>');
      $('deliveryPreview').src = `${path}/preview`;
    });
  }
  if (run.state === 'awaiting_delivery_approval') {
    button('review-delivery', 'Owner kiểm tra và duyệt bàn giao', () => {
      dialog('Duyệt đúng phiên bản sản phẩm', `<form id="deliveryReviewForm"><p class="hash">${escape(artifact.sha256)}</p><p class="helper">Ba bộ kiểm tra không chứng minh đầy đủ nghiệp vụ hoặc quyền sở hữu nội dung.</p><label class="check"><input id="deliveryAcceptance" type="checkbox" required> Tôi đã kiểm tra nội dung và tiêu chí thực tế.</label><label class="check"><input id="deliveryRights" type="checkbox" required> Tôi đã đối chiếu nguồn gốc và có quyền sử dụng nội dung.</label><button class="button primary" type="submit">Duyệt bản này để xuất ZIP</button></form>`);
      const key = crypto.randomUUID();
      form('deliveryReviewForm', () => post(`${path}/review`, { expectedSequence: run.lastSequence, artifactSha256: artifact.sha256, approved: true, acceptanceConfirmed: $('deliveryAcceptance').checked, rightsConfirmed: $('deliveryRights').checked }, key));
    }, true);
    button('reject-delivery', 'Yêu cầu sửa sản phẩm', async () => { await post(`${path}/review`, { expectedSequence: run.lastSequence, artifactSha256: artifact.sha256, approved: false, acceptanceConfirmed: false, rightsConfirmed: false }, crypto.randomUUID()); await refresh(); });
  }
  if (run.state === 'delivered') button('export-delivery', 'Tải ZIP sản phẩm và receipts', async () => {
    const response = await request(`${path}/export`); download(await response.blob(), `${run.runId}-delivery.zip`);
  }, true);
}
