import { escapeHtml as esc } from './presentation.js';
const $ = id => document.getElementById(id);
export function nextOwnerStep(data, run) {
  if (!data.projects.length) return { kind:'demo', title:'Bắt đầu với một project', text:'Bạn có thể tạo demo có sẵn code/test để xem đội ngũ làm việc, hoặc chọn project của bạn.', label:'Tạo / mở demo', target:'#companySetup' };
  if (!run) return { kind:'create', title:'Giao mục tiêu cho đội ngũ', text:'Mô tả kết quả mong muốn và điều kiện đạt bằng ngôn ngữ thông thường. Agent sẽ lập yêu cầu và kế hoạch.', label:'Giao việc mới', target:'#newSoftwareRun' };
  if (run.state === 'paused') return { kind:'resume', title:'Công việc đang tạm dừng', text:'Bấm Tiếp tục khi bạn đã sẵn sàng; app giữ nguyên checkpoint.', label:'Tiếp tục công việc', target:'#software-resume' };
  if (['completed','cancelled'].includes(run.state)) return { kind:'create', title:run.state === 'completed' ? 'Công việc đã hoàn tất' : 'Công việc đã hủy', text:'Bạn có thể giao một mục tiêu mới. Kết quả và lịch sử cũ vẫn được giữ.', label:'Giao việc tiếp theo', target:'#newSoftwareRun' };
  const cloud = run.jobs.find(j => j.round === run.round && j.status === 'waiting_cloud_approval');
  if (cloud) return { kind:'cloud', jobId:cloud.jobId, title:'AI cần bạn duyệt nội dung gửi đi', text:'Xem nội dung, dịch vụ nhận và giới hạn token. Duyệt xong agent sẽ chạy; key không nằm trong nội dung gửi.', label:'Xem & cho phép AI chạy', target:'#software-cloud-review' };
  if (run.state === 'awaiting_plan_approval') return { kind:'scope', title:'Kế hoạch đã có — bạn duyệt phạm vi thực hiện', text:'Kiểm tra mục tiêu, thư mục được sửa và lệnh kiểm thử trước khi cho đội ngũ bắt đầu triển khai.', label:'Xem phạm vi cần duyệt', target:'#software-preview-scope' };
  if (run.state === 'awaiting_final_approval') return { kind:'accept', title:'Đến bước bạn nghiệm thu', text:'Đối chiếu kết quả với yêu cầu và biên nhận kiểm thử. Bạn có thể yêu cầu sửa hoặc xác nhận bàn giao.', label:'Xem kết quả & nghiệm thu', target:'#software-final-review' };
  if (run.state === 'awaiting_release') return { kind:'delivery', title:'Chọn cách nhận kết quả', text:'Tải source/patch về trước. Tạo commit hoặc push Git là thao tác riêng, không bắt buộc.', label:'Tải kết quả', target:'#software-export' };
  if (run.state === 'waiting_input') return { kind:'input', title:'Agent này chưa được kết nối AI khi giao việc', text:'Kết nối AI cho nhân viên rồi tạo công việc mới để dùng cấu hình mới. Chế độ nâng cao vẫn cho phép nhập kết quả thủ công.', label:'Kết nối AI cho agent', target:'#companyConnectAI' };
  if (run.state === 'blocked') return { kind:'blocked', title:'Có một bước cần xử lý', text:'Xem nguyên nhân và hướng khắc phục trước khi thử lại. Thiếu Docker, key hoặc model sẽ không được tính là kiểm thử đạt.', label:'Xem lỗi & cách tiếp tục', target:'#software-retry' };
  return { kind:'working', title:'Đội ngũ đang thực hiện', text:'Bạn có thể xem tiến độ và trao đổi. App sẽ chỉ rõ khi cần bạn duyệt; lúc này chưa cần bấm tiếp.', label:'Xem tiến độ', target:'#workModeProgress' };
}

export class IdleCoach {
  constructor(resolve) {
    this.resolve = resolve; this.delay = 15000; this.lastActivity = Date.now(); this.snoozed = new Set();
    try {this.enabled=localStorage.getItem('aicos.idleHints')!=='off';} catch {this.enabled=true;}
    this.popup = document.createElement('aside'); this.popup.id = 'idleCoach'; this.popup.hidden = true; this.popup.className = 'idle-coach'; this.popup.setAttribute('role','dialog'); this.popup.setAttribute('aria-label','Gợi ý bước tiếp theo'); document.body.append(this.popup);
    const activity = event => { if (this.popup.contains(event?.target)) return; this.lastActivity = Date.now(); this.hide(); this.schedule(); };
    for (const name of ['pointermove','pointerdown','keydown','wheel','touchstart']) document.addEventListener(name, activity, { passive:true });
    document.addEventListener('visibilitychange', activity);
    this.timer = null; this.schedule();
    window.addEventListener('resize', () => this.position());
  }
  hide() { this.popup.hidden = true; this.anchor?.classList.remove('coach-target'); this.anchor?.removeAttribute('aria-describedby'); this.anchor = null; }
  schedule() { clearTimeout(this.timer); if (this.enabled) this.timer = setTimeout(() => this.show(), Math.max(50,this.delay - (Date.now()-this.lastActivity))); }
  show() {
    if (!this.enabled) return;
    if (document.hidden || $('detailDialog').open || document.activeElement?.matches('input,textarea,select,[contenteditable=true]')) { this.lastActivity = Date.now(); this.schedule(); return; }
    const step = this.resolve(); if (!step || step.kind === 'working' || this.snoozed.has(step.key)) return;
    this.step = step; this.anchor = document.querySelector(step.target);
    if (!this.anchor || !this.anchor.getClientRects().length) this.anchor = $('ownerGuideAction');
    if (!this.anchor?.getClientRects().length) return;
    this.anchor.classList.add('coach-target'); this.anchor.setAttribute('aria-describedby','idleCoachDescription');
    this.popup.innerHTML = `<span class="eyebrow">GỢI Ý SAU 15 GIÂY</span><h3>${esc(step.title)}</h3><p id="idleCoachDescription">${esc(step.text)}</p><div class="row-actions"><button id="coachGo" class="button primary">${esc(step.label)}</button><button id="coachDismiss" class="button">Đã hiểu</button><button id="coachDisable" class="button">Tắt gợi ý</button></div>`;
    this.popup.hidden = false; this.position();
    $('coachGo').onclick = () => { const action = this.step.action; this.hide(); action(); };
    $('coachDismiss').onclick = () => { this.snoozed.add(step.key); this.hide(); };
    $('coachDisable').onclick = () => this.toggle(false);
  }
  position() {
    if (this.popup.hidden || !this.anchor) return;
    const r = this.anchor.getBoundingClientRect(), width = Math.min(340,innerWidth-24); this.popup.style.width = width+'px';
    const height = this.popup.offsetHeight, visible = r.top >= 0 && r.bottom <= innerHeight;
    this.popup.style.left = Math.max(12,Math.min(innerWidth-width-12,r.left))+'px';
    this.popup.style.top = Math.max(12,Math.min(innerHeight-height-12, visible ? (r.bottom+height+16 < innerHeight ? r.bottom+12 : r.top-height-12) : innerHeight-height-12))+'px';
  }
  toggle(enabled) { this.enabled = enabled; try {localStorage.setItem('aicos.idleHints',enabled?'on':'off');} catch {} this.hide(); this.lastActivity=Date.now(); this.schedule(); if ($('idleHintsEnabled')) $('idleHintsEnabled').checked=enabled; }
  refresh() { if (!this.popup.hidden) { const current = this.resolve(); if (!current || current.key !== this.step.key) this.hide(); else if (!this.anchor?.isConnected) { this.anchor=document.querySelector(current.target); if(!this.anchor?.getClientRects().length) this.anchor=$('ownerGuideAction'); this.anchor?.classList.add('coach-target'); this.anchor?.setAttribute('aria-describedby','idleCoachDescription'); this.position(); } } }
}

export class GuidedWorkspace {
  constructor(w) { this.w=w; this.coach=new IdleCoach(() => this.step()); }
  step() {
    if (!this.w.data || !this.w.visible) return null;
    const step=nextOwnerStep(this.w.data,this.w.currentRun()); step.key=[this.w.companyId,this.w.currentRun()?.seed.runId,step.kind,step.jobId].join(':');
    step.action=()=>this.act(step).catch(e=>this.w.s.toast(e.message)); return step;
  }
  render() {
    const step=this.step(); if (!step) return;
    if (this.guideKey===step.key) {this.coach.refresh();return;} this.guideKey=step.key;
    $('ownerGuide').innerHTML=`<div><span class="eyebrow">BƯỚC TIẾP THEO DÀNH CHO BẠN</span><h3>${esc(step.title)}</h3><p>${esc(step.text)}</p></div><div><button class="button primary" id="ownerGuideAction">${esc(step.label)}</button><label class="check"><input id="idleHintsEnabled" type="checkbox" ${this.coach.enabled?'checked':''}>Gợi ý khi tạm dừng 15 giây</label></div>`;
    $('ownerGuideAction').onclick=step.action; $('idleHintsEnabled').onchange=e=>this.coach.toggle(e.target.checked); this.coach.refresh();
  }
  async act(step) {
    const w=this.w;
    if (step.kind==='demo') return w.setupGuide.open();
    if (step.kind==='create') return w.createRun();
    if (step.kind==='input') return this.connect(w.currentRun()?.jobs.find(j=>j.status==='waiting_input')?.agentId);
    if (step.kind==='cloud') return this.cloud(w.currentRun(),step.jobId);
    if (step.kind==='blocked') return this.problem();
    w.tab='workflows'; w.render();
    const target=document.querySelector(step.target);
    if (target?.matches('button')) target.click(); else target?.scrollIntoView({block:'center',behavior:'smooth'});
  }
  async connect(agentId=null) {
    const w=this.w, agents=w.state.agents??[], selected=agents.find(a=>a.agentId===agentId)??agents[0];
    w.s.dialog('Kết nối AI cho nhân viên','<p role="status">Đang đọc cấu hình nhân viên…</p>');
    const statuses=await w.s.api(w.base+'/agent-connections'); const c=w.data.agentConfigurations.find(c=>c.agentId===selected?.agentId), p=w.data.cloudProviders.find(p=>p.providerId===c?.providerId);
    w.s.dialog('Kết nối AI cho nhân viên',`<form id="agentConnectForm"><p>1. Chọn nhân viên → 2. Chọn dịch vụ và model → 3. Nhập key. Agent sẽ dùng kết nối này khi bạn giao công việc mới.</p><label for="connectAgent">Nhân viên</label><select id="connectAgent">${agents.map(a=>`<option value="${esc(a.agentId)}" ${a===selected?'selected':''}>${esc(a.name)}</option>`).join('')}</select><label for="connectProvider">Dịch vụ AI</label><select id="connectProvider"><option value="anthropic">Claude · Anthropic API</option><option value="claude-code">Claude Code · chạy CLI trên máy</option><option value="openai">OpenAI API</option><option value="gemini">Gemini API</option></select><p id="connectExplanation" class="helper">API chạy trực tiếp trong workflow, không cần cài CLI. Key này khác với đăng nhập website.</p><label for="connectSecret">API key của dịch vụ đã chọn</label><input type="password" id="connectSecret" autocomplete="off" required maxlength="8192"><button class="button" id="connectModels" type="button">Kiểm tra key & lấy danh sách model</button><p class="helper" id="connectModelStatus"></p><label for="connectModel">Model</label><input id="connectModel" list="connectModelList" required value="${esc(p?.modelId??'')}" placeholder="Chọn từ danh sách sau khi kiểm tra key"><datalist id="connectModelList"></datalist><label class="check"><input type="checkbox" id="connectConsent" required>Tôi cho phép nhân viên này dùng dịch vụ cloud. Từng nội dung công việc vẫn phải được xem trước và duyệt trước khi gửi.</label><p class="helper">Key chỉ giữ trong phiên làm việc, không lưu vào DB hay backup. Sau nâng cấp/khởi động lại, nhập lại key. ${statuses.find(s=>s.agentId===selected?.agentId)?.keyAvailable&&c?.providerId?'Nhân viên này đang có key trong phiên hiện tại.':''}</p><div class="row-actions"><button type="submit" class="button primary">Lưu kết nối cho nhân viên</button><button id="connectLocal" class="button" type="button">Dùng Ollama local</button></div><p id="connectResult" role="status"></p></form>`);
    $('connectProvider').value=c?.engine==='claude-code'?'claude-code':p?.providerType??'anthropic';
    $('connectProvider').onchange=()=>{ $('connectModel').value=''; $('connectModelList').replaceChildren(); $('connectExplanation').textContent=$('connectProvider').value==='claude-code'?'Cần Claude Code mới trên PATH. App dùng API key với chế độ bare, không dùng login thuê bao, không cấp quyền shell/file/MCP. Chạy setup --with-clis để cài.':'API chạy trực tiếp trong workflow; không cần CLI. Key này khác với đăng nhập website.'; };
    $('connectAgent').onchange=()=>{const id=$('connectAgent').value;if(id!==selected.agentId)this.connect(id).catch(e=>w.s.toast(e.message));};
    $('connectLocal').onclick=()=>w.setupGuide.open();
    $('connectModels').onclick=async()=>{ const b=$('connectModels'); b.disabled=true; try { const result=await w.s.api('/api/v1/agent-models',{method:'POST',body:JSON.stringify({providerType:$('connectProvider').value==='claude-code'?'anthropic':$('connectProvider').value,secret:$('connectSecret').value})}); $('connectModelList').innerHTML=result.models.map(m=>`<option value="${esc(m)}"></option>`).join(''); $('connectModelStatus').textContent=result.available?`Đọc được ${result.models.length} model. Chọn model tài khoản có quyền dùng.`:result.detail; if(result.models.length===1) $('connectModel').value=result.models[0]; }catch(e){$('connectModelStatus').textContent=e.message;}finally{b.disabled=false;} };
    $('agentConnectForm').onsubmit=async event=>{event.preventDefault();const button=event.submitter;button.disabled=true; const id=$('connectAgent').value, engine=$('connectProvider').value==='claude-code'?'claude-code':'api';
      try {const result=await w.s.api(`${w.base}/agents/${id}/connect`,{method:'POST',body:JSON.stringify({expectedVersion:c?.version??0,providerType:engine==='claude-code'?'anthropic':$('connectProvider').value,modelId:$('connectModel').value.trim(),secret:$('connectSecret').value,engine,allowCloud:$('connectConsent').checked})});$('connectSecret').value='';await w.s.refresh();w.s.dialog('Nhân viên đã được kết nối',`<p>${esc(result.detail)}</p><button class="button primary" id="connectAnother">Kết nối nhân viên khác</button><button class="button" id="connectCreate">Giao việc mới</button>`);w.bind('connectAnother',()=>this.connect(agents[(agents.indexOf(selected)+1)%agents.length]?.agentId));w.bind('connectCreate',()=>w.createRun());}catch(e){$('connectResult').textContent=e.message;}finally{button.disabled=false;}};
  }
  async cloud(run,jobId) {
    const w=this.w, preview=await w.s.api(`${w.base}/software-runs/${run.seed.runId}/jobs/${jobId}/cloud-preview`), p=preview.cloud.provider;
    w.s.dialog('Cho phép nhân viên gửi nội dung tới AI',`<h3>${esc(preview.name)} → ${esc(p.providerType==='anthropic'?'Claude':p.providerType)}</h3><p>Model: ${esc(p.modelId)} · ${preview.cloud.engine==='claude-code'?'Claude Code trên máy':'API trực tiếp'} · tối đa ${preview.maxOutputTokens} token đầu ra.</p><p>Phạm vi: yêu cầu, tài liệu và mã nguồn có trong nội dung bên dưới. Chưa gửi gì trước khi bạn bấm đồng ý. Việc gửi có thể phát sinh phí theo tài khoản của bạn.</p><details id="softwareCloudPayload"><summary>Nội dung chính xác sẽ gửi (không có API key)</summary><pre>${esc(preview.payload)}</pre></details><div class="row-actions"><button class="button primary" id="approveSoftwareCloud">Đồng ý gửi & chạy bước này</button><button class="button" id="cloudFixConnection">Nhập / sửa API key</button></div><p class="helper">Mỗi ngữ cảnh mới, kể cả agent xin đọc thêm file, cần duyệt lại. Quyết định này không cấp quyền chạy command cho model.</p>`);
    w.bind('cloudFixConnection',()=>this.connect(run.jobs.find(j=>j.jobId===jobId).agentId));
    w.bind('approveSoftwareCloud',async()=>{await w.mutate(`/software-runs/${run.seed.runId}/jobs/${jobId}/cloud-approve`,{expectedSequence:preview.lastSequence,payloadSha256:preview.payloadSha256});$('detailDialog').close();});
  }
  problem() {
    const w=this.w, run=w.currentRun(), failed=run.jobs.filter(j=>j.status==='failed'||j.status==='interrupted'), error=run.error??failed.at(-1)?.error??'Cần xem báo cáo.';
    const advice=/CREDENTIAL|CLAUDE|CLOUD|MODEL|PROVIDER/.test(error)?'Kiểm tra kết nối AI, key/quota và model. Run giữ route đã chụp; nếu đã đổi provider/model, tạo run mới.':/DOCKER|IMAGE|SANDBOX/.test(error)?'Mở Docker Desktop Linux containers hoặc kiểm tra daemon Ubuntu; dùng Thiết lập AI & Demo để kiểm tra lại.':'Đọc lỗi và bằng chứng của bước chưa đạt. Yêu cầu sửa nếu kết quả khác mong muốn; chỉ thử lại khi đã xử lý nguyên nhân.';
    w.s.dialog('Cách tiếp tục công việc',`<h3>Bước chưa đạt: ${esc(failed.map(j=>j.roleId.toUpperCase()).join(', ')||'kiểm tra runtime')}</h3><p>${esc(advice)}</p><details><summary>Chi tiết kỹ thuật để gửi người hỗ trợ</summary><pre>${esc(error)}</pre></details><div class="row-actions"><button class="button" id="problemConnect">Kết nối AI</button><button class="button" id="problemSetup">Kiểm tra máy / Docker</button><button class="button primary" id="problemRetry">Đã xử lý, thử lại</button><button class="button" id="problemReport">Xem báo cáo</button></div>`);
    w.bind('problemConnect',()=>this.connect(failed.at(-1)?.agentId));w.bind('problemSetup',()=>w.setupGuide.open());w.bind('problemReport',()=>{$('detailDialog').close();w.tab='workflows';w.render();$('softwareNodeInspector').scrollIntoView({block:'center'});});w.bind('problemRetry',async()=>{await w.mutate(`/software-runs/${run.seed.runId}/retry`,{expectedSequence:run.lastSequence});$('detailDialog').close();});
  }
  async updates() {
    const w=this.w, status=await w.s.api('/api/v1/updates/status'); let candidate=status.state;
    w.s.dialog('Cập nhật ứng dụng',`<p>Phiên bản: ${esc(status.build.version)}. ${esc(status.detail)}</p><p id="updateStatus" role="status">${esc(status.state.detail??'Bấm kiểm tra để xem kênh phát hành ổn định.')}</p><ol class="update-steps"><li>Kiểm tra bản mới</li><li>Tải & xác minh checksum</li><li>Sao lưu → nâng cấp → mở lại</li></ol><div class="row-actions"><button class="button" id="updateCheck" ${status.managed?'':'disabled'}>Kiểm tra cập nhật</button><button class="button" id="updateDownload" disabled>Tải bản mới</button><button class="button primary" id="updateApply" ${status.state.phase==='ready'?'':'disabled'}>Nâng cấp & khởi động lại</button></div><p class="helper">Đợi agent dừng trước khi nâng cấp. Launcher giữ snapshot dữ liệu và tự khôi phục nếu bản mới không khởi động được. Nguồn cập nhật: GitHub Releases của dự án; chỉ bản merge vào master và CI đạt mới được xuất bản.</p>`);
    for(const [id,path] of [['updateCheck','check'],['updateDownload','download']])$(id).onclick=async()=>{const button=$(id);button.disabled=true;try{$('updateStatus').textContent=path==='check'?'Đang kiểm tra…':'Đang tải và xác minh gói…';const result=await w.s.api('/api/v1/updates/'+path,{method:'POST',body:'{}'});candidate=result;$('updateStatus').textContent=result.detail;$('updateDownload').disabled=!result.available||result.phase==='ready';$('updateApply').disabled=result.phase!=='ready';}catch(e){$('updateStatus').textContent=e.message;}finally{button.disabled=false;}};
    $('updateApply').onclick=async()=>{const button=$('updateApply');button.disabled=true;try{await w.s.api('/api/v1/updates/apply',{method:'POST',body:JSON.stringify({tag:candidate.tag,sha256:candidate.sha256})});$('updateStatus').textContent='Đang sao lưu, nâng cấp và khởi động lại. Trang sẽ tự tải lại khi app sẵn sàng.';const poll=async(attempt=0)=>{try{await w.s.api('/api/session',{signal:AbortSignal.timeout(4000)});const state=await w.s.api('/api/v1/updates/status',{signal:AbortSignal.timeout(4000)});if(['installed','rolled_back','failed'].includes(state.state.phase)){location.reload();return;}}catch{}if(attempt<120)setTimeout(()=>poll(attempt+1),2000);else $('updateStatus').textContent='Ứng dụng chưa sẵn sàng. Giữ nguyên dữ liệu, kiểm tra cửa sổ launcher rồi mở lại trang.';};setTimeout(()=>poll(),2000);}catch(e){$('updateStatus').textContent=e.message;button.disabled=false;}};
  }
}
