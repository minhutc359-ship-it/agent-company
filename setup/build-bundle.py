#!/usr/bin/env python3
"""Package a published runtime and the installer without credentials or user data."""
import argparse
from pathlib import Path
import zipfile
import hashlib
import json
import os
import subprocess
p = argparse.ArgumentParser()
p.add_argument('--runtime', type=Path, required=True)
p.add_argument('--output', type=Path, required=True)
a = p.parse_args()
if not (a.runtime / 'AICompanyOS.Runtime.dll').is_file():
    raise SystemExit('Publish the runtime first.')
root = Path(__file__).resolve().parent
try: commit = os.environ.get('GITHUB_SHA') or subprocess.check_output(['git','rev-parse','HEAD'], cwd=root, text=True).strip()
except (OSError, subprocess.CalledProcessError): commit = 'source'
(a.runtime / 'build-info.json').write_text(json.dumps({'version':'2.5.0','commit':commit,'buildNumber':int(os.environ.get('GITHUB_RUN_NUMBER','0')),'protocol':1,'runtimeMajor':10}), encoding='utf-8')
files = [(root / n, n) for n in ['setup-aicos.py', 'update_engine.py', 'Setup-Windows.cmd', 'Start-Windows.cmd', 'Open-Agent-Terminal.cmd', 'setup-ubuntu.sh', 'README.vi.md']]
files += [(f, 'app/' + f.relative_to(a.runtime).as_posix()) for f in a.runtime.rglob('*') if f.is_file() and f.suffix not in ('.pdb', '.aicos')]
manifest = {name: hashlib.sha256(f.read_bytes()).hexdigest() for f, name in files}
a.output.parent.mkdir(parents=True, exist_ok=True)
with zipfile.ZipFile(a.output, 'w', zipfile.ZIP_DEFLATED, compresslevel=6) as z:
    for f, name in files:
        z.write(f, name)
    z.writestr('SHA256SUMS.json', json.dumps(manifest, indent=2) + '\n')
print(a.output)
