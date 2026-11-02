import assert from 'node:assert/strict';
import {test} from 'node:test';
import {nextOwnerStep} from '../src/AICompanyOS.Runtime/wwwroot/guidance.js';
const data={projects:[{}]},run=(state,jobs=[])=>({state,jobs,round:0});
test('new user gets a demo and then an ordinary objective, not JSON',()=>{assert.equal(nextOwnerStep({projects:[]},null).kind,'demo');assert.equal(nextOwnerStep(data,null).kind,'create');});
test('all Owner gates resolve to a concrete action',()=>{for(const [state,kind]of Object.entries({awaiting_plan_approval:'scope',awaiting_final_approval:'accept',awaiting_release:'delivery',waiting_input:'input',blocked:'blocked',paused:'resume',completed:'create',cancelled:'create'}))assert.equal(nextOwnerStep(data,run(state)).kind,kind);});
test('a pending cloud call is surfaced while other workers are busy',()=>{const step=nextOwnerStep(data,run('running',[{status:'waiting_cloud_approval',round:0,jobId:'exact-job'}]));assert.equal(step.kind,'cloud');assert.equal(step.jobId,'exact-job');});
test('paused/cancelled work never prompts approval of a queued cloud step',()=>{for(const state of ['paused','cancelled'])assert.notEqual(nextOwnerStep(data,run(state,[{status:'waiting_cloud_approval',round:0,jobId:'j'}])).kind,'cloud');});
test('old-round approvals do not appear as current actions',()=>assert.equal(nextOwnerStep(data,run('running',[{status:'waiting_cloud_approval',round:-1}])).kind,'working'));
