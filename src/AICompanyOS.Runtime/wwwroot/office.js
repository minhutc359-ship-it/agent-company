export const columns = 18;
export const rows = 12;
const desks = [
  ['ba', 2, 2, 'RESEARCH'], ['leader', 7, 2, 'PLANNING'], ['dev', 12, 2, 'ENGINEERING'],
  ['tester', 2, 7, 'TESTING'], ['soc', 7, 7, 'SECURITY'], ['qa', 12, 7, 'QUALITY'], ['subpm', 15, 7, 'REPORTS'], ['owner', 16, 1, 'OWNER']
];
export const obstacles = new Set(desks.flatMap(([, c, r]) => [-1, 0, 1].map(dx => `${c + dx},${r}`)));
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
const colors = { ba: '#819b69', leader: '#bc8b47', dev: '#678b96', tester: '#9f838f', soc: '#79866b', qa: '#ba936e', subpm: '#8d93a5' };
const stateLabels = { queued: 'Đang chờ AI', working: 'AI local đang xử lý', idle: 'Chưa có invocation', handoff_pending: 'Chờ ACK', waiting_approval: 'Chờ Owner', paused: 'Đã tạm dừng', blocked: 'Cần sửa lại' };
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
export class OfficeScene {
  constructor(svg, onSelect) {
    this.svg = svg; this.onSelect = onSelect; this.actors = new Map(); this.paused = false;
    this.motion = matchMedia('(prefers-reduced-motion: reduce)');
    this.motion.addEventListener('change', () => { this.lastTime = 0; this.kick(); });
    document.addEventListener('visibilitychange', () => { this.lastTime = 0; this.kick(); });
    this.drawMap();
  }
  drawMap() {
    this.svg.replaceChildren();
    for (let r = 0; r < rows; r++) for (let c = 0; c < columns; c++) {
      const [x, y] = iso(c, r);
      this.svg.append(svgNode('polygon', { points: `${x},${y} ${x + 24},${y + 12} ${x},${y + 24} ${x - 24},${y + 12}`, fill: (r + c) % 2 ? '#e1e8d5' : '#e6ebda', stroke: '#f2f4e8', 'stroke-width': '.5' }));
    }
    desks.forEach(([role, c, r, label]) => {
      const [x, y] = iso(c, r);
      const g = svgNode('g');
      g.append(svgNode('polygon', { points: `${x - 45},${y + 8} ${x + 24},${y - 8} ${x + 49},${y + 5} ${x - 22},${y + 22}`, fill: '#d4b992', stroke: '#bca883' }));
      g.append(svgNode('polygon', { points: `${x - 45},${y + 8} ${x - 22},${y + 22} ${x - 22},${y + 35} ${x - 45},${y + 22}`, fill: '#b59c76' }));
      g.append(svgNode('polygon', { points: `${x - 22},${y + 22} ${x + 49},${y + 5} ${x + 49},${y + 18} ${x - 22},${y + 35}`, fill: '#c6ad86' }));
      g.append(svgNode('rect', { x: x - 10, y: y - 14, width: 22, height: 16, rx: 2, fill: role === 'owner' ? '#a4b27b' : '#6b8478' }));
      g.append(svgNode('text', { x, y: y - 27, 'text-anchor': 'middle', class: 'office-zone' }, label));
      this.svg.append(g);
    });
    this.svg.append(svgNode('text', { x: 50, y: 480, class: 'office-zone' }, 'AI COMPANY OS / LIVING OFFICE'));
    this.svg.append(svgNode('text', { x: 50, y: 498, class: 'office-bubble' }, 'Projection từ dữ liệu đã lưu · Hoạt ảnh không khởi chạy AI'));
    this.layer = svgNode('g'); this.svg.append(this.layer);
  }
  clear() { this.actors.clear(); this.active = null; this.lastTime = 0; this.drawMap(); }
  setPaused(paused) { this.paused = paused; this.lastTime = 0; this.kick(); }
  setVisible(visible) { this.visible = visible; this.lastTime = 0; this.kick(); }
  update(snapshot) {
    const homes = new Set(), destinations = new Set();
    const ordered = [...snapshot.agents].sort((a, b) => Number(!primaryIds.has(a.agent.agentId)) - Number(!primaryIds.has(b.agent.agentId)));
    for (const item of ordered) {
      const id = item.agent.agentId;
      let actor = this.actors.get(id);
      const desk = desks.find(([role]) => item.agent.roleIds.includes(role)) ?? desks[0];
      const preferredHome = primaryIds.has(id) ? { c: desk[1], r: desk[2] + 1 } : { c: 5, r: 10 };
      const home = nearestFree(preferredHome, spacedCells(homes)) ?? nearestFree(preferredHome, homes); homes.add(key(home));
      if (!actor) {
        const occupied = new Set([...this.actors.values()].map(a => key(a.cell)));
        const cell = nearestFree(home, spacedCells(occupied)) ?? nearestFree(home, occupied);
        actor = { id, cell, home, lastSeq: -1, target: cell, item, group: this.avatar(item) };
        this.actors.set(id, actor);
      }
      actor.item = item; actor.home = home;
      if (actor.lastSeq !== item.sequence || actor.lastState !== item.state) {
        let preferred = home;
        if (item.state === 'waiting_approval') preferred = { c: 16, r: 3 };
        if (item.state === 'handoff_pending' && item.agent.roleIds.includes('ba')) preferred = { c: 6, r: 4 };
        if (item.state === 'paused') preferred = actor.cell;
        actor.target = nearestFree(preferred, destinations);
        actor.lastSeq = item.sequence; actor.lastState = item.state;
      }
      // Reserve every destination, including agents whose state did not change.
      if (destinations.has(key(actor.target))) actor.target = nearestFree(actor.home, destinations);
      destinations.add(key(actor.target));
      actor.group.querySelector('title').textContent = `${item.agent.name} · ${stateLabels[item.state]} · event #${item.sequence}`;
      actor.group.querySelector('.office-bubble').textContent = stateLabels[item.state] ?? item.state;
      actor.group.setAttribute('aria-label', `${item.agent.name}: ${stateLabels[item.state] ?? item.state}`);
      if (this.active?.actor !== actor) this.position(actor, actor.cell);
    }
    this.kick();
  }
  avatar(item) {
    const g = svgNode('g', { class: 'office-agent', tabindex: '0', role: 'button' });
    g.append(svgNode('title'));
    g.append(svgNode('ellipse', { cx: 0, cy: 12, rx: 14, ry: 6, fill: '#71845b', opacity: '.2' }));
    g.append(svgNode('circle', { cx: 0, cy: -3, r: 17, fill: '#f4f5e7', class: 'avatar-ring' }));
    g.append(svgNode('path', { d: 'M-10 8 L-9 -2 Q0 -9 9 -2 L10 8Z', fill: colors[item.agent.roleIds[0]] ?? '#849378' }));
    g.append(svgNode('circle', { cx: 0, cy: -12, r: 7, fill: '#e9c5a3' }));
    g.append(svgNode('path', { d: 'M-7 -14 Q-6 -24 3 -20 Q9 -20 7 -10 L4 -16Z', fill: '#465344' }));
    g.append(svgNode('text', { x: 0, y: 30, 'text-anchor': 'middle', class: 'office-label' }, primaryIds.has(item.agent.agentId) ? item.agent.name.slice(0, 19) : `Agent ${this.actors.size + 1}`));
    g.append(svgNode('text', { x: 0, y: 42, 'text-anchor': 'middle', class: 'office-bubble' }));
    g.addEventListener('click', () => this.onSelect(item.agent.agentId));
    g.addEventListener('keydown', e => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); this.onSelect(item.agent.agentId); } });
    this.layer.append(g); return g;
  }
  position(actor, cell) { const [x, y] = iso(cell.c, cell.r); actor.group.setAttribute('transform', `translate(${x} ${y + 7})`); }
  kick() {
    if (this.scheduled || this.paused || !this.visible || document.hidden) return;
    this.scheduled = true; requestAnimationFrame(time => this.tick(time));
  }
  tick(time) {
    this.scheduled = false;
    if (this.paused || !this.visible || document.hidden) return;
    const dt = this.lastTime ? Math.min(50, time - this.lastTime) : 0; this.lastTime = time;
    const occupied = new Set([...this.actors.values()].map(a => key(a.cell)));
    if (!this.active) {
      for (const actor of this.actors.values()) {
        if (key(actor.cell) === key(actor.target)) continue;
        const blockers = new Set(occupied); blockers.delete(key(actor.cell));
        const path = findPath(actor.cell, actor.target, blockers);
        if (path.length) { this.active = { actor, next: this.motion.matches ? path.at(-1) : path[0], elapsed: 0 }; break; }
      }
    }
    if (!this.active) { this.lastTime = 0; return; }
    const move = this.active;
    move.elapsed += this.motion.matches ? 260 : dt;
    const progress = Math.min(1, move.elapsed / 260);
    this.position(move.actor, { c: move.actor.cell.c + (move.next.c - move.actor.cell.c) * progress, r: move.actor.cell.r + (move.next.r - move.actor.cell.r) * progress });
    if (progress === 1) { move.actor.cell = move.next; this.active = null; }
    this.kick();
  }
}
