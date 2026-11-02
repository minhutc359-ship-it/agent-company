export const escapeHtml = value => String(value ?? '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[c]);

// Keep complete identifiers in an explicit inspector, never in the everyday reading flow.
export function technicalDetails(value, title = 'Chi tiết kỹ thuật') {
  return `<details class="technical-details"><summary>${escapeHtml(title)}</summary><pre>${escapeHtml(JSON.stringify(value, null, 2))}</pre></details>`;
}

export function agentName(agents, id) {
  return id === 'owner' ? 'Bạn' : agents?.find(agent => agent.agentId === id)?.name ?? 'Nhân viên';
}

export function runName(state, id) {
  if (id === 'lobby') return 'Trao đổi chung';
  return state?.workbench?.runs?.find(run => run.seed.runId === id)?.seed.objective
    ?? state?.runs?.find(run => run.runId === id)?.objective
    ?? state?.sources?.find(source => source.sourceId === id)?.title
    ?? 'Công việc đã lưu';
}

export function branchName(branches, runId, id) {
  return id === 'main' ? 'Nhánh chính' : branches?.find(branch => branch.runId === runId && branch.branchId === id)?.name ?? 'Nhánh hội thoại';
}

export const providerName = type => ({ openai: 'OpenAI', anthropic: 'Anthropic', gemini: 'Gemini' })[type] ?? 'Cloud';
