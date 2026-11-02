import { mkdir, writeFile, access } from 'node:fs/promises';
import { execFile } from 'node:child_process';
import { promisify } from 'node:util';
import { resolve, join } from 'node:path';
const destination = process.argv[2];
if (!destination || process.argv.length !== 3) throw new Error('Usage: node examples/create-demo-repository.mjs /absolute/path/new-demo-repository');
const root = resolve(destination);
try { await access(root); throw new Error('Destination already exists; choose a new directory.'); } catch (error) { if (error.code !== 'ENOENT') throw error; }
for (const name of ['src', 'tests', 'docs']) await mkdir(join(root, name), { recursive: true });
await writeFile(join(root, 'src/validator.cjs'), 'exports.validate = value => value != null;\n');
await writeFile(join(root, 'tests/validator.test.cjs'), "const test = require('node:test'); const assert = require('node:assert/strict'); const { validate } = require('../src/validator.cjs'); test('[AC-001] non-empty validation', () => { assert.equal(validate(''), false); assert.equal(validate('valid'), true); });\n");
await writeFile(join(root, 'docs/NOTES.md'), 'Document the validation behavior here.\n');
await writeFile(join(root, 'package.json'), JSON.stringify({ name: 'aicos-validation-demo', version: '1.0.0', private: true }, null, 2) + '\n');
const git = promisify(execFile);
for (const args of [['init', '-b', 'master'], ['config', 'user.name', 'Local demo'], ['config', 'user.email', 'demo@localhost'], ['add', '.'], ['commit', '-m', 'Validation demo baseline']]) await git('git', ['-C', root, ...args]);
console.log(JSON.stringify({ projectId: 'validation-demo', name: 'Validation demo', repositoryPath: root, readPaths: ['*'], writePaths: ['src/', 'tests/', 'docs/'], dockerImage: 'node:22-alpine', build: { name: 'build', executable: 'node', arguments: ['--check', 'src/validator.cjs'], timeoutSeconds: 30 }, test: { name: 'test', executable: 'node', arguments: ['--test', 'tests/validator.test.cjs'], timeoutSeconds: 30 }, baseBranch: 'master' }, null, 2));
console.log('The test intentionally fails before implementation. Explicitly install node:22-alpine, then add this project and create a Company software run.');
