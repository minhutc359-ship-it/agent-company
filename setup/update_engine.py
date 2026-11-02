"""Versioned update supervisor. Never imports or runs downloaded installer code."""
import argparse
import hashlib
import http.cookiejar
import json
import os
from pathlib import Path
import re
import shutil
import stat
import subprocess
import sys
import time
import urllib.error
import urllib.parse
import urllib.request
import zipfile

REPOSITORY = 'minhutc359-ship-it/agent-company'
API = 'https://api.github.com/repos/' + REPOSITORY + '/releases/latest'
BUNDLE = 'AICompanyOS-Setup.zip'
MAX_BYTES = 100 * 1024 * 1024
TAG = re.compile(r'^build-([1-9][0-9]{0,11})$')

class UpdateError(Exception):
    pass

def read_json(path, default=None):
    return json.loads(Path(path).read_text(encoding='utf-8')) if Path(path).is_file() else default

def atomic_json(path, value):
    path = Path(path); path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_suffix('.tmp')
    temporary.write_text(json.dumps(value, ensure_ascii=True, indent=2), encoding='utf-8')
    os.replace(temporary, path)

def child(root, name):
    root = Path(root).resolve(); path = (root / name).resolve()
    if root not in path.parents:
        raise UpdateError('Đường dẫn cập nhật không hợp lệ.')
    return path

def validate_url(url):
    parsed = urllib.parse.urlparse(url)
    if parsed.scheme != 'https' or parsed.username or parsed.password or parsed.port not in (None, 443):
        raise UpdateError('Nguồn cập nhật không hợp lệ.')
    if parsed.hostname == 'api.github.com' and parsed.path == '/repos/' + REPOSITORY + '/releases/latest': return
    if parsed.hostname == 'github.com' and parsed.path.startswith('/' + REPOSITORY + '/releases/download/'): return
    if parsed.hostname == 'release-assets.githubusercontent.com': return
    raise UpdateError('Chỉ tải bản phát hành của repository đã cấu hình.')

class Redirects(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        validate_url(newurl)
        return super().redirect_request(req, fp, code, msg, headers, newurl)

def fetch(url, limit=2*1024*1024):
    validate_url(url)
    opener = urllib.request.build_opener(Redirects())
    request = urllib.request.Request(url, headers={'Accept':'application/vnd.github+json', 'User-Agent':'AICompanyOS-Updater'})
    with opener.open(request, timeout=45) as response:
        validate_url(response.url); data = response.read(limit + 1)
        if len(data) > limit: raise UpdateError('Gói cập nhật vượt giới hạn dung lượng.')
        return data

def info(app):
    return read_json(Path(app) / 'build-info.json', {'version':'2.5.0-dev', 'buildNumber':0, 'commit':'source', 'protocol':1, 'runtimeMajor':10})

def current_app(root, prefix):
    active = read_json(Path(prefix) / 'updates/active.json')
    return child(Path(prefix) / 'updates/versions', active['tag']) / 'app' if active else Path(root) / 'app'

def latest(app, reader=fetch):
    installed = info(app)
    try:
        release = json.loads(reader(API))
    except urllib.error.HTTPError as error:
        if error.code == 404: return {'available':False, 'detail':'Chưa có bản phát hành ổn định. CI sẽ xuất bản sau khi thay đổi được merge vào master và kiểm thử đạt.', 'current':installed}
        raise
    tag = release.get('tag_name', ''); match = TAG.fullmatch(tag)
    if not match or release.get('draft') or release.get('prerelease'): raise UpdateError('Metadata bản phát hành không hợp lệ.')
    assets = [a for a in release.get('assets', []) if a.get('name') == BUNDLE]
    if len(assets) != 1: raise UpdateError('Bản phát hành chưa có gói cài hoàn chỉnh.')
    asset = assets[0]; digest = asset.get('digest', '')
    if not re.fullmatch(r'sha256:[a-f0-9]{64}', digest) or not 0 < asset.get('size', 0) <= MAX_BYTES:
        raise UpdateError('Thiếu checksum SHA256 hoặc dung lượng gói không hợp lệ.')
    validate_url(asset['browser_download_url'])
    number = int(match[1]); available = number > installed.get('buildNumber', 0)
    return {'available':available, 'tag':tag, 'buildNumber':number, 'sha256':digest[7:], 'size':asset['size'],
            'url':asset['browser_download_url'], 'current':installed, 'detail':'Có bản mới sẵn sàng tải.' if available else 'Bạn đang dùng bản hiện tại hoặc bản mới hơn kênh ổn định.'}

def extract_verified(archive, destination):
    destination = Path(destination).resolve()
    with zipfile.ZipFile(archive) as z:
        entries = z.infolist()
        if len(entries) > 5000 or sum(i.file_size for i in entries) > 400*1024*1024: raise UpdateError('Gói giải nén vượt giới hạn.')
        names = set()
        for entry in entries:
            name = entry.filename
            segments = name.rstrip('/').split('/')
            reserved = {'CON','PRN','AUX','NUL',*[f'COM{i}' for i in range(1,10)],*[f'LPT{i}' for i in range(1,10)]}
            if name.casefold() in names or any(s.endswith(('.', ' ')) or s.split('.')[0].upper() in reserved for s in segments) or '\\' in name or ':' in name or name.startswith('/') or '..' in name.split('/') or ((entry.external_attr >> 16) & 0o170000) == 0o120000:
                raise UpdateError('Gói chứa đường dẫn hoặc link không an toàn.')
            names.add(name.casefold()); child(destination, name)
        z.extractall(destination)
    manifest = read_json(destination / 'SHA256SUMS.json')
    if not isinstance(manifest, dict) or 'app/AICompanyOS.Runtime.dll' not in manifest or 'app/build-info.json' not in manifest:
        raise UpdateError('Gói thiếu runtime hoặc manifest.')
    if set(manifest) != {i.filename for i in entries if not i.is_dir()} - {'SHA256SUMS.json'}:
        raise UpdateError('Manifest không bao phủ toàn bộ gói.')
    for name, digest in manifest.items():
        path = child(destination, name)
        if not isinstance(digest, str) or hashlib.sha256(path.read_bytes()).hexdigest() != digest: raise UpdateError('Checksum file không khớp: ' + name)
    build = info(destination / 'app')
    if build.get('protocol') != 1 or build.get('runtimeMajor') != 10: raise UpdateError('Bản này cần bộ setup mới; dữ liệu hiện tại chưa thay đổi.')
    return build

def stage(root, prefix, reader=fetch):
    prefix = Path(prefix); updates = prefix / 'updates'; state = read_json(updates / 'state.json', {})
    candidate = latest(current_app(root, prefix), reader)
    if not candidate.get('available'): raise UpdateError(candidate['detail'])
    if state.get('tag') != candidate['tag'] or state.get('sha256') != candidate['sha256']:
        raise UpdateError('Bản phát hành đã thay đổi. Bấm kiểm tra cập nhật lại.')
    atomic_json(updates / 'state.json', {**candidate, 'phase':'downloading'})
    archive = updates / 'download.zip'; pending = updates / 'staging'
    try:
        raw = reader(candidate['url'], MAX_BYTES)
        if len(raw) != candidate['size'] or hashlib.sha256(raw).hexdigest() != candidate['sha256']: raise UpdateError('Checksum gói tải xuống không khớp.')
        archive.write_bytes(raw)
        if pending.exists(): shutil.rmtree(pending)
        pending.mkdir()
        build = extract_verified(archive, pending)
        if build.get('buildNumber') != candidate['buildNumber']: raise UpdateError('Phiên bản trong gói không khớp bản đã duyệt.')
        destination = child(updates / 'versions', candidate['tag']); destination.parent.mkdir(exist_ok=True)
        if destination.exists(): shutil.rmtree(destination)
        os.replace(pending, destination)
        result = {**candidate, 'phase':'ready', 'version':build['version'], 'detail':'Đã tải và xác minh. Có thể nâng cấp khi không còn tác vụ đang chạy.'}
        atomic_json(updates / 'state.json', result); return result
    except Exception:
        atomic_json(updates / 'state.json', {**candidate, 'phase':'failed', 'detail':'Tải hoặc xác minh thất bại; ứng dụng và dữ liệu hiện tại được giữ nguyên.'})
        raise
    finally:
        archive.unlink(missing_ok=True)
        if pending.exists(): shutil.rmtree(pending)

def healthy(expected, process):
    jar = http.cookiejar.CookieJar()
    opener = urllib.request.build_opener(urllib.request.ProxyHandler({}), urllib.request.HTTPCookieProcessor(jar))
    for _ in range(60):
        if process.poll() is not None: return False
        try:
            with opener.open('http://127.0.0.1:4173/api/session', timeout=2) as r: r.read(4096)
            with opener.open('http://127.0.0.1:4173/api/v1/health', timeout=2) as r: response = json.loads(r.read(16384))
            if response.get('build', {}).get('commit') == expected.get('commit') and response.get('build', {}).get('buildNumber') == expected.get('buildNumber'): return True
        except (OSError, ValueError): pass
        time.sleep(.5)
    return False

def rollback(prefix):
    prefix = Path(prefix); updates = prefix / 'updates'; journal = read_json(updates / 'transaction.json')
    if not journal: return
    backup = child(updates / 'backups', journal.get('backup', journal['tag']))
    data = prefix / 'data'
    # Restore the full closed-store snapshot as well as the previous application pointer.
    if backup.is_dir():
        failed = child(updates, 'failed-data-' + str(time.time_ns()))
        if data.exists(): os.replace(data, failed)
        shutil.copytree(backup, data)
    if journal['old'] is None: (updates / 'active.json').unlink(missing_ok=True)
    else: atomic_json(updates / 'active.json', journal['old'])
    atomic_json(updates / 'state.json', {'phase':'rolled_back', 'detail':'Bản mới không khởi động được. Đã khôi phục ứng dụng và dữ liệu trước nâng cấp.'})
    (updates / 'transaction.json').unlink(missing_ok=True)

def activate(prefix):
    prefix = Path(prefix); updates = prefix / 'updates'; state = read_json(updates / 'state.json', {})
    tag = state.get('tag', '')
    if state.get('phase') != 'ready' or not TAG.fullmatch(tag): raise UpdateError('Chưa có gói đã xác minh để nâng cấp.')
    version = child(updates / 'versions', tag)
    # Reverify all bytes after download and again before executing the new version.
    manifest = read_json(version / 'SHA256SUMS.json', {})
    if not manifest or any(hashlib.sha256(child(version, n).read_bytes()).hexdigest() != d for n, d in manifest.items()): raise UpdateError('Gói đã bị thay đổi sau khi tải.')
    backup = child(updates / 'backups', tag); backup.parent.mkdir(exist_ok=True)
    if backup.exists(): backup = child(updates / 'backups', tag + '-retry-' + str(time.time_ns()))
    pending_backup = child(updates / 'backups', 'pending-' + backup.name)
    if pending_backup.exists():
        for file in pending_backup.rglob('*'):
            if file.is_file(): file.chmod(stat.S_IWRITE | stat.S_IREAD)
        shutil.rmtree(pending_backup)
    if (prefix / 'data').exists(): shutil.copytree(prefix / 'data', pending_backup)
    else: pending_backup.mkdir()
    os.replace(pending_backup, backup)
    atomic_json(updates / 'transaction.json', {'tag':tag, 'backup':backup.name, 'old':read_json(updates / 'active.json')})
    atomic_json(updates / 'active.json', {'tag':tag})

def supervise(root, prefix, env, probe=healthy):
    root = Path(root); prefix = Path(prefix); updates = prefix / 'updates'
    rollback(prefix)  # interrupted activation is rolled back before any process starts
    attempted = False
    while True:
        app = current_app(root, prefix); child_env = {**env, 'AICOS_INSTALL_ROOT':str(root.resolve()), 'AICOS_INSTALL_PREFIX':str(prefix.resolve()), 'AICOS_PYTHON':sys.executable}
        process = subprocess.Popen(['dotnet', str(app / 'AICompanyOS.Runtime.dll')], cwd=app, env=child_env)
        if attempted:
            if probe(info(app), process):
                (updates / 'transaction.json').unlink(missing_ok=True)
                atomic_json(updates / 'state.json', {'phase':'installed', 'detail':'Nâng cấp thành công. Dữ liệu và model được giữ nguyên.', 'current':info(app)})
            else:
                process.terminate()
                try: process.wait(timeout=20)
                except subprocess.TimeoutExpired: process.kill(); process.wait()
                rollback(prefix); attempted = False; continue
        try: code = process.wait()
        except KeyboardInterrupt:
            process.terminate()
            try: process.wait(timeout=25)
            except subprocess.TimeoutExpired: process.kill(); process.wait()
            return 0
        request = updates / 'apply-request.json'
        if code != 42 or not request.is_file(): return code
        request.unlink()
        try: activate(prefix); attempted = True
        except Exception as error:
            atomic_json(updates / 'state.json', {'phase':'failed', 'detail':str(error)}); rollback(prefix); attempted = False

def main():
    parser = argparse.ArgumentParser(); parser.add_argument('action', choices=['check','stage','status']); parser.add_argument('--root', type=Path, required=True); parser.add_argument('--prefix', type=Path, required=True)
    args = parser.parse_args(); updates = args.prefix / 'updates'
    if args.action == 'check':
        result = {**latest(current_app(args.root, args.prefix)), 'phase':'checked'}; atomic_json(updates / 'state.json', result)
    elif args.action == 'stage': result = stage(args.root, args.prefix)
    else: result = read_json(updates / 'state.json', {'phase':'idle','current':info(current_app(args.root,args.prefix))})
    print(json.dumps(result, ensure_ascii=True))

if __name__ == '__main__':
    try: main()
    except Exception as error:
        print(json.dumps({'error':str(error)}, ensure_ascii=True)); sys.exit(1)
