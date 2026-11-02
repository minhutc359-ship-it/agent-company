"""CI only: publish an immutable build tag after all required jobs passed."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess

p=argparse.ArgumentParser(); p.add_argument('--bundle',type=Path,required=True); a=p.parse_args()
if os.environ.get('GITHUB_EVENT_NAME')!='push' or os.environ.get('GITHUB_REF')!='refs/heads/master': raise SystemExit('Only verified master push builds can publish.')
repo='minhutc359-ship-it/agent-company'
if os.environ.get('GITHUB_REPOSITORY')!=repo: raise SystemExit('Unexpected repository.')
tag='build-'+str(int(os.environ['GITHUB_RUN_NUMBER'])); sha=os.environ['GITHUB_SHA']
bundle=a.bundle.parent/'AICompanyOS-Setup.zip'; shutil.copyfile(a.bundle,bundle)
existing=subprocess.run(['gh','api','repos/'+repo+'/releases/tags/'+tag],capture_output=True,text=True)
if existing.returncode==0:
    release=json.loads(existing.stdout); digest='sha256:'+hashlib.sha256(bundle.read_bytes()).hexdigest()
    if release['target_commitish']!=sha or not any(x.get('name')==bundle.name and x.get('digest')==digest for x in release['assets']): raise SystemExit('An immutable build tag already exists with different bytes; refusing to replace it.')
    print('This exact release is already published.')
else:
    subprocess.run(['gh','release','create',tag,str(bundle),'--repo',repo,'--target',sha,'--title','AI Company OS 2.5 · '+tag,'--notes-file','docs/RELEASE_2.5.md','--latest'],check=True)
