import { teamAtlas, decorAtlas, avatarIndex } from './office-assets.js';
export const columns = 18;
export const rows = 12;
const desks = [
  ['ba', 2, 2, 'RESEARCH'], ['leader', 7, 2, 'PLANNING'], ['dev', 12, 2, 'ENGINEERING'],
  ['tester', 2, 7, 'TESTING'], ['soc', 7, 7, 'SECURITY'], ['qa', 12, 7, 'QUALITY'], ['subpm', 15, 7, 'REPORTS'], ['owner', 16, 1, 'OWNER']
];
export const obstacles = new Set([...desks.flatMap(([, c, r]) => [-1, 0, 1].map(dx => `${c + dx},${r}`)), "7,5", "8,5", "9,5"]);
const key = p => `${p.c},${p.r}`;
const inside = p => Number.isInteger(p.c) && Number.isInteger(p.r) && p.c >= 0 && p.c < columns && p.r >= 0 && p.r < rows;
const distance = (a, b) => Math.abs(a.c - b.c) + Math.abs(a.r - b.r);
export function findPath(start, goal, occupied = new Set()) {
  if (!inside(start) || !inside(goal) || obstacles.has(key(start)) || obstacles.has(key(goal)) || occupied.has(key(goal))) return [];
  const open = [{ ...start, g: 0, path: [] }], best = new Map([[key(start), 0]]);
  while (open.length) {
    open.sort((a, b) => a.g + distance(a, goal) - b.g - distance(b, goal));
    const current = open.shift();
    if (key(current) === key(goal)) return current.path;
    for (const [dc, dr] of [[1, 0], [-1, 0], [0, 1], [0, -1]]) {
      const next = { c: current.c + dc, r: current.r + dr }, cost = current.g + 1;
      if (!inside(next) || obstacles.has(key(next)) || occupied.has(key(next)) || (best.get(key(next)) ?? Infinity) <= cost) continue;
      best.set(key(next), cost);
      open.push({ ...next, g: cost, path: [...current.path, next] });
    }
  }
  return [];
}
function nearestFree(target, occupied) {
  return Array.from({ length: columns * rows }, (_, i) => ({ c: i % columns, r: Math.floor(i / columns) }))
    .filter(p => !obstacles.has(key(p)) && !occupied.has(key(p))).sort((a, b) => distance(a, target) - distance(b, target))[0];
}
const iso = (c, r) => [470 + (c - r) * 24, 60 + (c + r) * 12];
const stateLabels = { researching: 'Đang phân tích', planning: 'Đang lập kế hoạch', coding: 'Đang tạo patch', testing: 'Đang chạy test', reviewing: 'Đang kiểm tra', waiting_input: 'Chờ input', meeting: 'Đang họp', talking: 'Đang tham vấn', queued: 'Đang chờ AI', working: 'Đang xử lý', idle: 'Sẵn sàng', handoff_pending: 'Chờ ACK', waiting_approval: 'Chờ Owner', paused: 'Đã tạm dừng', blocked: 'Cần sửa lại' };
function spacedCells(cells) {
  const points = [...cells].map(cell => { const [c, r] = cell.split(',').map(Number); return iso(c, r); });
  const blocked = new Set(cells);
  for (let c = 0; c < columns; c++) for (let r = 0; r < rows; r++) {
    const [x, y] = iso(c, r);
    if (points.some(([px, py]) => Math.hypot(x - px, y - py) < 56)) blocked.add(`${c},${r}`);
  }
  return blocked;
}
const primaryIds = new Set(desks.map(([role]) => `${role}-agent`));
const ns = 'http://www.w3.org/2000/svg';
function svgNode(tag, attrs = {}, text) {
  const node = document.createElementNS(ns, tag);
  Object.entries(attrs).forEach(([name, value]) => node.setAttribute(name, value));
  if (text !== undefined) node.textContent = text;
  return node;
}
function atlasSprite(atlas, index, size, attrs = {}) {
  const sprite = svgNode('svg', { x: -size / 2, y: -size + 16, width: size, height: size, viewBox: `${index % 4 * 512} ${Math.floor(index / 4) * 512} 512 512`, overflow: 'hidden', 'aria-hidden': 'true', ...attrs });
  sprite.append(svgNode('image', { href: atlas, width: 2048, height: 1024, preserveAspectRatio: 'none' })); return sprite;
}
export class OfficeScene {
  constructor(svg, onSelect) {
    this.svg = svg; this.onSelect = onSelect; this.actors = new Map(); this.moves = new Map(); this.paused = false;
    this.camera = { x: 0, y: 0, width: 940, height: 530 }; this.zoomLevel = 1; this.theme = 'night';
    this.motion = matchMedia('(prefers-reduced-motion: reduce)');
    this.motion.addEventListener('change', () => { this.lastTime = 0; this.kick(); });
    document.addEventListener('visibilitychange', () => { this.lastTime = 0; this.svg.dataset.documentHidden = String(document.hidden); this.kick(); });
    this.drawMap(); this.bindCamera();
  }
  drawMap() {
    this.svg.replaceChildren(); this.svg.dataset.theme = this.theme;
    this.svg.append(svgNode('rect', { width: 940, height: 530, rx: 18, class: 'office-backdrop' }));
    this.svg.append(svgNode('path', { d: 'M182 192L470 48L902 264L902 205L470 -11L182 133Z', class: 'office-wall' }));
    this.svg.append(svgNode('path', { d: 'M182 192L470 48L902 264', class: 'office-wall-light', fill: 'none', 'stroke-width': 3 }));
    for (let i = 0; i < 5; i++) {
      const x = 499 + i * 74, y = 28 + i * 37;
      this.svg.append(svgNode('path', { d: `M${x} ${y}l54 27v35l-54 -27Z`, class: 'office-window' }));
      this.svg.append(svgNode('path', { d: `M${x + 8} ${y + 28}l6 -8l7 7l5 -13l9 16l10 5`, class: 'office-skyline', fill: 'none', 'stroke-width': 3 }));
    }
    for (let r = 0; r < rows; r++) for (let c = 0; c < columns; c++) {
      const [x, y] = iso(c, r);
      this.svg.append(svgNode('polygon', { points: `${x},${y} ${x + 24},${y + 12} ${x},${y + 24} ${x - 24},${y + 12}`, class: `office-tile ${(r + c) % 2 ? 'alternate' : ''}` }));
    }
    this.depthItems = []; this.depthOrder = ''; this.layer = svgNode('g', { class: 'office-depth-layer' }); this.svg.append(this.layer);
    const prop = (index, c, r, size, name) => {
      const [x, y] = iso(c, r), group = svgNode('g', { transform: `translate(${x} ${y + 5})`, class: 'office-decor', 'aria-hidden': 'true' });
      group.append(svgNode('ellipse', { cx: 0, cy: 12, rx: size * .32, ry: size * .10, class: 'office-decor-shadow' })); group.append(atlasSprite(decorAtlas, index, size));
      this.layer.append(group); this.depthItems.push({ id: name, depth: c + r, group }); return group;
    };
    desks.forEach(([role, c, r, label]) => {
      const [x, y] = iso(c, r), plate = svgNode('g', { class: 'office-zone-plate' });
      plate.append(svgNode('rect', { x: x - 46, y: y + 28, width: 92, height: 15, rx: 4 }));
      plate.append(svgNode('text', { x, y: y + 39, 'text-anchor': 'middle', class: 'office-zone' }, label)); this.svg.insertBefore(plate, this.layer);
      prop(0, c, r, 102, 'desk-' + role);
    });
    prop(1, 8, 5, 128, 'meeting-table');
    // Decorative props sit at the room perimeter, outside the navigable tiles.
    prop(2, -1, 3, 91, 'library'); prop(2, -1, 6, 80, 'archive');
    prop(3, -1, -1, 76, 'plant-north'); prop(3, 18, 10, 76, 'plant-south'); prop(3, -1, 10, 78, 'plant-west');
    prop(4, 2, 12, 112, 'lounge'); prop(5, 5, -1, 83, 'planning-board'); prop(6, 14, -1, 78, 'servers'); prop(7, 16, -1, 70, 'lamp');
    this.effects = svgNode('g', { class: 'office-effects', 'aria-hidden': 'true' }); this.svg.append(this.effects);
    this.svg.append(svgNode('text', { x: 36, y: 486, class: 'office-brand' }, 'THE COMPANY / LIVING STUDIO'));
    this.svg.append(svgNode('text', { x: 36, y: 505, class: 'office-caption' }, 'Nhân viên, tiến độ và trao đổi từ dữ liệu đã lưu'));
    this.sortDepth();
  }
  clear() { this.actors.clear(); this.moves.clear(); this.lastTime = 0; this.resetCamera(); this.drawMap(); }
  setPaused(paused) { this.paused = paused; this.svg.dataset.paused = String(paused); this.lastTime = 0; this.kick(); }
  setVisible(visible) { this.visible = visible; this.svg.dataset.active = String(visible); this.lastTime = 0; this.kick(); }
  setTheme(theme) { this.theme = theme === 'day' ? 'day' : 'night'; this.svg.dataset.theme = this.theme; }
  select(id) { for (const actor of this.actors.values()) actor.group.dataset.selected = String(actor.id === id); }
  setFilter(ids) { const visible = new Set(ids); for (const actor of this.actors.values()) actor.group.dataset.dimmed = String(!visible.has(actor.id)); }
  update(snapshot) {
    const homes = new Set(), destinations = new Set();
    const ordered = [...snapshot.agents].sort((a, b) => Number(!primaryIds.has(a.agent.agentId)) - Number(!primaryIds.has(b.agent.agentId)));
    const currentIds = new Set(ordered.map(a => a.agent.agentId));
    for (const [id, actor] of this.actors) if (!currentIds.has(id)) { actor.group.remove(); this.actors.delete(id); this.moves.delete(id); }
    for (const item of ordered) {
      const id = item.agent.agentId; let actor = this.actors.get(id);
      const desk = desks.find(([role]) => item.agent.roleIds.includes(role)) ?? desks[0];
      const preferredHome = primaryIds.has(id) ? { c: desk[1], r: desk[2] + 1 } : { c: 5, r: 10 };
      const home = nearestFree(preferredHome, spacedCells(homes)) ?? nearestFree(preferredHome, homes); homes.add(key(home));
      if (!actor) {
        const occupied = new Set([...this.actors.values()].map(a => key(a.cell)));
        const cell = nearestFree(home, spacedCells(occupied)) ?? nearestFree(home, occupied);
        actor = { id, cell, home, lastSeq: -1, target: cell, item, group: this.avatar(item) }; this.actors.set(id, actor);
      }
      actor.item = item; actor.home = home;
      if (actor.lastSeq !== item.sequence || actor.lastState !== item.state) {
        let preferred = home;
        if (item.state === 'waiting_approval') preferred = { c: 16, r: 3 };
        if (item.state === 'handoff_pending' && item.agent.roleIds.includes('ba')) preferred = { c: 6, r: 4 };
        if (item.state === 'meeting') preferred = { c: 8, r: 4 };
        if (item.state === 'talking') preferred = { c: 10, r: 5 };
        if (item.state === 'paused') preferred = actor.cell;
        actor.target = nearestFree(preferred, destinations); actor.lastSeq = item.sequence; actor.lastState = item.state;
      }
      if (destinations.has(key(actor.target))) actor.target = nearestFree(actor.home, destinations);
      destinations.add(key(actor.target));
      actor.group.querySelector('title').textContent = `${item.agent.name} · ${stateLabels[item.state] ?? item.state}${item.publicSummary ? ' · ' + item.publicSummary : ''}`;
      actor.group.querySelector('.office-label').textContent = item.agent.name.length > 20 ? item.agent.name.slice(0, 19) + '…' : item.agent.name;
      actor.group.querySelector('.office-bubble').textContent = item.publicSummary ? 'Xem trao đổi ↗' : (stateLabels[item.state] ?? item.state);
      const role = item.activeRoleId ?? item.agent.roleIds[0];
      actor.group.querySelector('.office-character-sprite').setAttribute('viewBox', `${avatarIndex(role) % 4 * 512} ${Math.floor(avatarIndex(role) / 4) * 512} 512 512`);
      actor.group.dataset.state = item.state;
      actor.group.querySelector('.office-state-icon').textContent = ({ researching: '⌕', planning: '▦', coding: '⌨', working: '⌨', testing: '✓', reviewing: '◇', meeting: '••', talking: '••', handoff_pending: '↗', waiting_approval: '?', blocked: '!' })[item.state] ?? '';
      actor.group.setAttribute('aria-label', `${item.agent.name}: ${stateLabels[item.state] ?? item.state}`);
      if (!this.moves.has(actor.id)) this.position(actor, actor.cell);
    }
    this.svg.dataset.meeting = String(snapshot.agents.some(a => a.state === 'meeting')); this.svg.dataset.crowded = String(snapshot.agents.length > 12);
    this.effects.replaceChildren();
    for (const actor of this.actors.values()) {
      if (actor.item.state !== 'handoff_pending') continue;
      const receiver = [...this.actors.values()].find(a => a.item.agent.roleIds.includes('leader')); if (!receiver || receiver.id === actor.id) continue;
      const [x, y] = iso(actor.cell.c, actor.cell.r), [tx, ty] = iso(receiver.cell.c, receiver.cell.r);
      this.effects.append(svgNode('path', { d: `M${x} ${y - 15}Q${(x + tx) / 2} ${Math.min(y, ty) - 90} ${tx} ${ty - 15}`, class: 'office-handoff-trail', fill: 'none' }));
    }
    this.sortDepth(); this.kick();
  }
  avatar(item) {
    const g = svgNode('g', { class: 'office-agent', tabindex: '0', role: 'button' }); g.append(svgNode('title'));
    g.append(svgNode('ellipse', { cx: 0, cy: 12, rx: 20, ry: 7, class: 'avatar-ring' }));
    const body = svgNode('g', { class: 'office-avatar-body' }); body.append(atlasSprite(teamAtlas, avatarIndex(item.agent.roleIds[0]), 76, { class: 'office-character-sprite' }));
    body.append(svgNode('text', { x: 26, y: -47, 'text-anchor': 'middle', class: 'office-state-icon' })); g.append(body);
    g.append(svgNode('rect', { x: -56, y: 23, width: 112, height: 27, rx: 5, class: 'office-name-plate' }));
    g.append(svgNode('text', { x: 0, y: 34, 'text-anchor': 'middle', class: 'office-label' }));
    g.append(svgNode('text', { x: 0, y: 45, 'text-anchor': 'middle', class: 'office-bubble' }));
    g.onclick = () => this.onSelect(item.agent.agentId);
    g.onkeydown = e => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); this.onSelect(item.agent.agentId); } };
    g.querySelector('.office-bubble').onclick = e => { const current = this.actors.get(item.agent.agentId)?.item; if (current?.publicMessageId && this.onPublicMessage) { e.stopPropagation(); this.onPublicMessage(current.publicMessageId, current.publicMessageRunId ?? current.runId); } };
    this.layer.append(g); return g;
  }
  position(actor, cell) { const [x, y] = iso(cell.c, cell.r); actor.group.setAttribute('transform', `translate(${x} ${y + 7})`); }
  sortDepth() {
    const all = [...this.depthItems, ...[...this.actors.values()].map(a => ({ id: a.id, depth: a.cell.c + a.cell.r + .1, group: a.group }))].sort((a, b) => a.depth - b.depth || a.id.localeCompare(b.id));
    const order = all.map(i => i.id).join('|'); if (order !== this.depthOrder) { for (const item of all) this.layer.append(item.group); this.depthOrder = order; }
  }
  applyCamera() { const c = this.camera; this.svg.setAttribute('viewBox', `${c.x} ${c.y} ${c.width} ${c.height}`); this.svg.dataset.zoomed = String(c.width < 940); }
  resetCamera() { this.followed = null; this.zoomLevel = 1; this.camera = { x: 0, y: 0, width: 940, height: 530 }; for (const a of this.actors.values()) a.group.dataset.followed = 'false'; this.applyCamera(); }
  zoom(delta) { this.followed = null; const old = this.camera; this.zoomLevel = Math.max(1, Math.min(2.5, this.zoomLevel + delta)); const width = 940 / this.zoomLevel, height = 530 / this.zoomLevel; this.camera = { x: Math.max(0, Math.min(940 - width, old.x + (old.width - width) / 2)), y: Math.max(0, Math.min(530 - height, old.y + (old.height - height) / 2)), width, height }; this.applyCamera(); }
  bindCamera() {
    this.svg.addEventListener('pointerdown', e => { if (e.target.closest('.office-agent') || this.camera.width >= 940) return; this.followed = null; this.pan = { x: e.clientX, y: e.clientY, origin: { ...this.camera }, pointer: e.pointerId }; this.svg.setPointerCapture(e.pointerId); });
    this.svg.addEventListener('pointermove', e => { if (!this.pan || this.pan.pointer !== e.pointerId) return; const bounds = this.svg.getBoundingClientRect(), p = this.pan; this.camera.x = Math.max(0, Math.min(940 - this.camera.width, p.origin.x - (e.clientX - p.x) * this.camera.width / bounds.width)); this.camera.y = Math.max(0, Math.min(530 - this.camera.height, p.origin.y - (e.clientY - p.y) * this.camera.height / bounds.height)); this.applyCamera(); });
    this.svg.addEventListener('pointerup', () => this.pan = null); this.svg.addEventListener('pointercancel', () => this.pan = null);
  }
  follow(id) { const same = this.followed === id; this.resetCamera(); if (!same && this.actors.has(id)) { this.followed = id; this.actors.get(id).group.dataset.followed = 'true'; this.followCamera(); } this.kick(); }
  followCamera() { const [x, y] = iso(this.actors.get(this.followed).cell.c, this.actors.get(this.followed).cell.r); this.camera = { x: Math.max(0, Math.min(340, x - 300)), y: Math.max(0, Math.min(170, y - 180)), width: 600, height: 360 }; this.applyCamera(); }
  kick() { if (this.scheduled || this.paused || !this.visible || document.hidden) return; this.scheduled = true; requestAnimationFrame(time => this.tick(time)); }
  tick(time) {
    this.scheduled = false; if (this.paused || !this.visible || document.hidden) return;
    const dt = this.lastTime ? Math.min(50, time - this.lastTime) : 0; this.lastTime = time;
    if (this.moves.size === 0) {
      const steps = reserveSteps([...this.actors.values()].map(a => ({ id: a.id, cell: a.cell, target: a.target })));
      for (const step of steps) { const actor = this.actors.get(step.id); this.moves.set(step.id, { actor, next: step.next, elapsed: 0 }); actor.group.dataset.moving = 'true'; }
    }
    if (this.moves.size === 0) { this.lastTime = 0; return; }
    for (const [id, move] of this.moves) {
      move.elapsed += this.motion.matches ? 260 : dt; const progress = Math.min(1, move.elapsed / 260);
      this.position(move.actor, { c: move.actor.cell.c + (move.next.c - move.actor.cell.c) * progress, r: move.actor.cell.r + (move.next.r - move.actor.cell.r) * progress });
      if (progress === 1) { move.actor.cell = move.next; move.actor.group.dataset.moving = 'false'; this.moves.delete(id); }
    }
    this.sortDepth(); if (this.followed && this.actors.has(this.followed)) this.followCamera(); this.kick();
  }
}

export function reserveSteps(actors) {
  const occupied = new Set(actors.map(a => key(a.cell))), reserved = new Set(), moves = [];
  for (const actor of actors) {
    if (key(actor.cell) === key(actor.target)) continue;
    const blockers = new Set([...occupied, ...reserved]); blockers.delete(key(actor.cell));
    const path = findPath(actor.cell, actor.target, blockers);
    if (!path.length) continue;
    const next = path[0]; reserved.add(key(next)); moves.push({ id: actor.id, next });
  }
  return moves;
}
