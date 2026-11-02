import assert from 'node:assert/strict';
import test from 'node:test';
import { findPath, obstacles } from '../src/AICompanyOS.Runtime/wwwroot/office.js';
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
