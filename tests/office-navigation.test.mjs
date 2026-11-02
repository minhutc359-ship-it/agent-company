import assert from 'node:assert/strict';
import test from 'node:test';
import { findPath, obstacles, reserveSteps, columns, rows } from '../src/AICompanyOS.Runtime/wwwroot/office.js';
const key = p => `${p.c},${p.r}`;
test('handoff path uses adjacent walkable tiles around desks', () => {
  const start = { c: 2, r: 3 }, goal = { c: 7, r: 3 };
  const path = findPath(start, goal, new Set(['4,3', '5,3', '6,3']));
  assert.deepEqual(path.at(-1), goal);
  let previous = start;
  for (const point of path) {
    assert.equal(Math.abs(previous.c - point.c) + Math.abs(previous.r - point.r), 1);
    assert.equal(obstacles.has(key(point)), false);
    assert.equal(new Set(['4,3', '5,3', '6,3']).has(key(point)), false);
    previous = point;
  }
});
test('occupied destinations and furniture cannot be crossed', () => {
  assert.deepEqual(findPath({ c: 2, r: 3 }, { c: 7, r: 3 }, new Set(['7,3'])), []);
  assert.deepEqual(findPath({ c: 2, r: 3 }, { c: 2, r: 2 }), []);
  assert.deepEqual(findPath({ c: 2, r: 2 }, { c: 2, r: 3 }), []);
});
test('blocked or invalid routes return no path', () => {
  const start = { c: 0, r: 0 }, goal = { c: 6, r: 4 };
  assert.deepEqual(findPath(start, goal, new Set(['1,0', '0,1'])), []);
  assert.deepEqual(findPath({ c: -1, r: 0 }, goal), []);
  assert.deepEqual(findPath(start, { c: 18, r: 12 }), []);
  assert.deepEqual(findPath(start, start), []);
});

for (const count of [2, 7, 25]) test(`${count} simultaneous agents reserve distinct walkable steps without swapping`, () => {
  const cells = Array.from({ length: columns * rows }, (_, i) => ({ c: i % columns, r: Math.floor(i / columns) })).filter(p => !obstacles.has(key(p)));
  const actors = cells.slice(0, count).map((cell, i) => ({ id: `agent-${i}`, cell, target: cells.at(-i - 1) }));
  let totalMoves = 0;
  for (let tick = 0; tick < 100; tick++) {
    const moves = reserveSteps(actors), occupied = new Set(actors.map(a => key(a.cell)));
    assert.equal(new Set(moves.map(m => key(m.next))).size, moves.length);
    for (const move of moves) {
      const actor = actors.find(a => a.id === move.id);
      assert.equal(occupied.has(key(move.next)), false);
      assert.equal(obstacles.has(key(move.next)), false);
      assert.equal(Math.abs(actor.cell.c - move.next.c) + Math.abs(actor.cell.r - move.next.r), 1);
    }
    for (const move of moves) actors.find(a => a.id === move.id).cell = move.next;
    assert.equal(new Set(actors.map(a => key(a.cell))).size, count);
    totalMoves += moves.length;
  }
  assert.ok(totalMoves > count, 'Every fixture must produce real navigation progress');
});
test('occupied agents cannot swap through one another', () => {
  assert.deepEqual(reserveSteps([{ id: 'a', cell: { c: 0, r: 0 }, target: { c: 1, r: 0 } }, { id: 'b', cell: { c: 1, r: 0 }, target: { c: 0, r: 0 } }]), []);
});
