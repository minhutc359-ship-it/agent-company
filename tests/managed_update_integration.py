"""Actual published .NET → update API → supervisor → healthy restart/rollback.
No vendor download, credentials, Ollama or Docker is used by this update test.
"""
import argparse
import hashlib
import http.cookiejar
import json
import os
from pathlib import Path
import shutil
import signal
import stat
import subprocess
import sys
import tempfile
import time
import urllib.request

parser=argparse.ArgumentParser();parser.add_argument('--runtime',type=Path,required=True);args=parser.parse_args()
source=Path(__file__).resolve().parents[1];root=Path(tempfile.mkdtemp(prefix='aicos-managed-update-')).resolve();prefix=root/'installed';process=None
dotnet=os.environ.get('AICOS_DOTNET','dotnet');base='http://127.0.0.1:4173'
def metadata(app,number):
    app.joinpath('build-info.json').write_text(json.dumps({'version':'2.5.0','buildNumber':number,'commit':str(number)*40,'protocol':1,'runtimeMajor':10}),encoding='utf-8')
def stage(number,broken=False):
    version=prefix/'updates/versions'/('build-'+str(number));shutil.copytree(args.runtime,version/'app');metadata(version/'app',number)
    if broken:(version/'app/AICompanyOS.Runtime.dll').write_bytes(b'intentionally invalid runtime for rollback verification')
    version.joinpath('SHA256SUMS.json').write_text(json.dumps({p.relative_to(version).as_posix():hashlib.sha256(p.read_bytes()).hexdigest() for p in version.rglob('*') if p.is_file()}),encoding='utf-8')
    (prefix/'updates/state.json').write_text(json.dumps({'phase':'ready','tag':'build-'+str(number),'sha256':'fixture-'+str(number)}),encoding='utf-8')
def call(path,body=None):
    opener=urllib.request.build_opener(urllib.request.ProxyHandler({}),urllib.request.HTTPCookieProcessor(http.cookiejar.CookieJar()))
    with opener.open(base+'/api/session',timeout=2) as response:response.read()
    request=urllib.request.Request(base+'/api/v1/'+path,data=None if body is None else json.dumps(body).encode(),headers={'Origin':base,'Content-Type':'application/json'})
    with opener.open(request,timeout=5) as response:return json.loads(response.read())
def wait(predicate,label):
    until=time.monotonic()+60
    while time.monotonic()<until:
        try:
            if predicate():return
        except (OSError,ValueError):pass
        if process.poll() is not None:raise RuntimeError('Supervisor stopped during '+label)
        time.sleep(.3)
    raise RuntimeError('Timeout '+label)
try:
    shutil.copytree(args.runtime,root/'app');metadata(root/'app',1)
    shutil.copyfile(source/'setup/update_engine.py',root/'update_engine.py')
    prefix.mkdir();(prefix/'models').mkdir();(prefix/'models/fixture-weights').write_bytes(b'model-must-not-change')
    env=dict(os.environ,AICOS_DATA_DIR=str(prefix/'data'),PATH=str(Path(dotnet).parent)+os.pathsep+os.environ.get('PATH',''))
    subprocess.run([dotnet,str(root/'app/AICompanyOS.Runtime.dll'),'--initialize-local'],env=env,cwd=root/'app',check=True,stdout=subprocess.DEVNULL)
    note=prefix/'data/demo/validation-project/docs/OWNER.txt';note.write_text('Keep this user file across updates.',encoding='utf-8')
    log=root/'supervisor.log'
    with log.open('wb') as output:
        process=subprocess.Popen([sys.executable,'-c','import update_engine,os;raise SystemExit(update_engine.supervise(os.getcwd(),os.path.join(os.getcwd(),"installed"),dict(os.environ)))'],cwd=root,env=env,stdout=output,stderr=output,start_new_session=os.name!='nt')
    wait(lambda:call('health')['build']['buildNumber']==1,'initial application startup')
    stage(2)
    try:call('updates/apply',{'tag':'build-999','sha256':'stale'})
    except urllib.error.HTTPError as error:assert error.code==409
    else:raise AssertionError('Stale update approval was accepted')
    assert call('health')['build']['buildNumber']==1
    call('updates/apply',{'tag':'build-2','sha256':'fixture-2'})
    wait(lambda:call('health')['build']['buildNumber']==2 and call('updates/status')['state'].get('phase')=='installed','healthy automatic update')
    assert note.read_text(encoding='utf-8')=='Keep this user file across updates.'
    before=call('companies/aicos-demo/workbench');assert len(before['projects'])==1
    stage(3,broken=True);call('updates/apply',{'tag':'build-3','sha256':'fixture-3'})
    wait(lambda:call('health')['build']['buildNumber']==2 and call('updates/status')['state'].get('phase')=='rolled_back','automatic rollback after failed startup')
    assert note.read_text(encoding='utf-8')=='Keep this user file across updates.'
    assert (prefix/'models/fixture-weights').read_bytes()==b'model-must-not-change'
    assert (prefix/'updates/backups/build-2').is_dir() and (prefix/'updates/backups/build-3').is_dir()
    assert len(call('companies/aicos-demo/workbench')['agentConfigurations'])==len(before['agentConfigurations'])
    print('PASS: actual published runtime update API, healthy supervisor restart, broken-runtime rollback, SQLite/demo/config/user-file/model preservation.')
except Exception:
    if (root/'supervisor.log').exists():print((root/'supervisor.log').read_text(encoding='utf-8',errors='replace')[-8000:],file=sys.stderr)
    raise
finally:
    if process and process.poll() is None:
        if os.name=='nt':subprocess.run(['taskkill','/PID',str(process.pid),'/T','/F'],stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL)
        else:os.killpg(process.pid,signal.SIGTERM)
        try:process.wait(timeout=15)
        except subprocess.TimeoutExpired:process.kill();process.wait()
    for path in root.rglob('*'):
        if path.is_file():path.chmod(stat.S_IWRITE|stat.S_IREAD)
    shutil.rmtree(root)
