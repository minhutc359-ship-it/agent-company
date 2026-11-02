#!/usr/bin/env python3
"""AI Company OS installer/doctor. Standard library only; Windows and Ubuntu x64/arm64.
No credentials are collected. --check/--plan never install or start anything.
"""
import argparse
from contextlib import closing
import hashlib
import json
import os
from pathlib import Path
import platform
import re
import shlex
import shutil
import sqlite3
import subprocess
import sys
import tarfile
import tempfile
import time
import urllib.error
import urllib.request
import webbrowser
import zipfile
import update_engine

BASE = Path(__file__).resolve().parent
IS_WINDOWS = os.name == 'nt'
MODEL_PATTERN = re.compile(r'^[A-Za-z0-9][A-Za-z0-9._:/-]{0,119}$')
URLS = {'dotnet': 'https://dot.net/v1/dotnet-install.' + ('ps1' if IS_WINDOWS else 'sh'),
        'ollama': 'https://ollama.com/install.sh', 'docker': 'https://get.docker.com'}
ALLOWED_HOSTS = {'dot.net', 'builds.dotnet.microsoft.com', 'dotnet.microsoft.com', 'nodejs.org', 'ollama.com', 'get.docker.com'}

class SetupError(Exception):
    pass

def run(args, *, env=None, cwd=None, capture=False, timeout=None):
    # Argument arrays only. No model/user value is ever evaluated as shell text.
    try:
        result = subprocess.run([str(x) for x in args], cwd=cwd, env=env, text=True, timeout=timeout or (30 if capture else 1800),
                                stdout=subprocess.PIPE if capture else None, stderr=subprocess.STDOUT if capture else None)
    except subprocess.TimeoutExpired as error:
        raise SetupError('Command timed out: ' + str(args[0])) from error
    if result.returncode:
        raise SetupError('Command failed (' + str(result.returncode) + '): ' + str(args[0]))
    return result.stdout.strip() if capture else ''

def get_json(url, *, data=None, limit=4*1024*1024):
    req = urllib.request.Request(url, data=json.dumps(data).encode() if data is not None else None,
                                 headers={'Content-Type': 'application/json'})
    # Local diagnostics never use a system HTTP proxy.
    opener = urllib.request.build_opener(urllib.request.ProxyHandler({})) if url.startswith('http://127.0.0.1:') else urllib.request.build_opener()
    with opener.open(req, timeout=15) as response:
        raw = response.read(limit + 1)
        if len(raw) > limit:
            raise SetupError('Response exceeds limit')
        return json.loads(raw)

def download(url, destination, limit=250*1024*1024):
    from urllib.parse import urlparse
    if urlparse(url).scheme != 'https' or urlparse(url).hostname not in ALLOWED_HOSTS:
        raise SetupError('Unsupported download origin')
    destination = Path(destination)
    with urllib.request.urlopen(url, timeout=60) as response, destination.open('wb') as target:
        if urlparse(response.url).hostname not in ALLOWED_HOSTS:
            raise SetupError('Unexpected download redirect')
        count = 0
        while True:
            chunk = response.read(1024*1024)
            if not chunk:
                break
            count += len(chunk)
            if count > limit:
                raise SetupError('Download exceeds limit')
            target.write(chunk)
    return destination

def safe_extract(archive, destination):
    destination = Path(destination).resolve()
    def safe(name):
        normalized = name.replace('\\', '/')
        if normalized.startswith('/') or ':' in normalized or '..' in normalized.split('/'):
            raise SetupError('Unsafe archive member')
        path = (destination / normalized).resolve()
        if path != destination and destination not in path.parents:
            raise SetupError('Archive escapes destination')
    if zipfile.is_zipfile(archive):
        with zipfile.ZipFile(archive) as source:
            for item in source.infolist():
                safe(item.filename)
                if (item.external_attr >> 16) & 0o170000 == 0o120000:
                    raise SetupError('Symbolic link in ZIP')
            source.extractall(destination)
    else:
        with tarfile.open(archive) as source:
            members = source.getmembers()
            for item in members:
                safe(item.name)
                if item.isdev() or item.isfifo() or item.issym() or item.islnk():
                    continue
            # Skip links, including npm launch symlinks; we create a fixed local launcher below.
            regular = [m for m in members if m.isfile() or m.isdir()]
            options = {'filter': 'data'} if hasattr(tarfile, 'data_filter') else {}
            source.extractall(destination, members=regular, **options)

def command(name, paths=()):
    found = shutil.which(name)
    if found:
        return found
    for path in paths:
        if Path(path).is_file():
            return str(path)
    return None

def major(executable, flag='--version'):
    try:
        return int(re.search(r'\d+', run([executable, flag], capture=True)).group()) if executable else 0
    except (SetupError, OSError, AttributeError):
        return 0

def environment(prefix, saved=None):
    env = dict(os.environ)
    directories = [prefix / '.dotnet', prefix / 'tools' / 'bin', prefix / 'cli' if IS_WINDOWS else prefix / 'cli/bin']
    if saved and saved.get('node_bin'):
        directories.insert(0, Path(saved['node_bin']))
    if IS_WINDOWS:
        directories.extend([Path(os.environ.get('ProgramFiles', 'C:/Program Files')) / 'Git/cmd',
                            Path(os.environ.get('ProgramFiles', 'C:/Program Files')) / 'Docker/Docker/resources/bin',
                            Path(os.environ.get('LOCALAPPDATA', '')) / 'Programs/Ollama'])
    env['PATH'] = os.pathsep.join(str(p) for p in directories) + os.pathsep + env.get('PATH', '')
    env.update({'DOTNET_CLI_TELEMETRY_OPTOUT': '1', 'DOTNET_NOLOGO': '1', 'AICOS_DATA_DIR': str(prefix / 'data'),
                'AICOS_OLLAMA_URL': 'http://127.0.0.1:11435/', 'OLLAMA_HOST': '127.0.0.1:11435',
                'OLLAMA_NO_CLOUD': '1', 'OLLAMA_MODELS': str(prefix / 'models'), 'AICOS_DOCKER_ENDPOINT': docker_args()[2]})
    return env

def verify_bundle():
    base = BASE.resolve()
    manifest = base / 'SHA256SUMS.json'
    if not manifest.is_file():
        raise SetupError('SHA256SUMS.json is missing. Extract the complete setup ZIP before running it.')
    entries = json.loads(manifest.read_text(encoding='utf-8'))
    if not isinstance(entries, dict) or len(entries) > 5000 or any(not isinstance(n, str) or not isinstance(v, str) or not re.fullmatch(r'[a-f0-9]{64}', v) for n, v in entries.items()):
        raise SetupError('Invalid bundle manifest')
    if 'app/AICompanyOS.Runtime.dll' not in entries or 'setup-aicos.py' not in entries:
        raise SetupError('Incomplete bundle manifest')
    for name, expected in entries.items():
        path = (base / name).resolve()
        if base not in path.parents or not path.is_file() or hashlib.sha256(path.read_bytes()).hexdigest() != expected:
            raise SetupError('Bundle checksum mismatch: ' + name)

def docker_args():
    endpoint = 'npipe:////./pipe/docker_engine' if IS_WINDOWS else 'unix:///var/run/docker.sock'
    return ['docker', '--host', endpoint]

def check_sqlite(prefix):
    try:
        profile = json.loads((prefix / 'data/storage.json').read_text())
        if profile.get('backend') != 'sqlite':
            return False
        generation = profile.get('generation')
        if generation is not None and not re.fullmatch(r'[A-Za-z0-9_-]{1,100}', generation):
            return False
        path = prefix / 'data'
        if generation:
            path = path / 'stores' / generation
        with closing(sqlite3.connect((path / 'events.sqlite').as_uri() + '?mode=ro', uri=True, timeout=3)) as db:
            return db.execute('PRAGMA quick_check').fetchone()[0] == 'ok' and db.execute('SELECT COUNT(*) FROM events').fetchone()[0] > 0
    except (OSError, ValueError, sqlite3.Error):
        return False

def check(prefix, saved):
    env = environment(prefix, saved)
    old = os.environ.get('PATH', '')
    os.environ['PATH'] = env['PATH']
    try:
        dotnet, git, node, ollama = (command(x) for x in ('dotnet', 'git', 'node', 'ollama'))
        try:
            sdks = run([dotnet, '--list-sdks'], env=env, capture=True) if dotnet else ''
        except SetupError:
            sdks = ''
        try:
            verify_bundle(); valid_bundle = True
        except (OSError, ValueError, SetupError):
            valid_bundle = False
        result = {'dotnet10': bool(re.search(r'^10\.', sdks, re.M)), 'git': bool(git), 'node22plus': major(node) >= 22,
                  'ollama_installed': bool(ollama), 'runtime_bundle': valid_bundle,
                  'docker_linux': False, 'demo_image': False, 'ollama_local': False, 'model': False,
                  'sqlite': check_sqlite(prefix)}
        try:
            result['docker_linux'] = run(docker_args() + ['info', '--format', '{{.OSType}}'], env=env, capture=True) == 'linux'
            result['demo_image'] = bool(run(docker_args() + ['image', 'inspect', 'node:22-alpine', '--format', '{{.Id}}'], env=env, capture=True))
        except (SetupError, OSError):
            pass
        try:
            get_json('http://127.0.0.1:11435/api/version')
            result['ollama_local'] = True
            names = [m['name'] for m in get_json('http://127.0.0.1:11435/api/tags').get('models', [])]
            model = saved.get('model')
            if model and model in names:
                info = get_json('http://127.0.0.1:11435/api/show', data={'model': model})
                lengths = [v for k, v in info.get('model_info', {}).items() if k.endswith('.context_length') and isinstance(v, int)]
                result['model'] = info.get('details', {}).get('format') == 'gguf' and max(lengths, default=0) >= 16384 and 'completion' in info.get('capabilities', []) and not info.get('remote_host') and not info.get('remote_model')
        except (OSError, ValueError, SetupError):
            pass
        required = [v for k, v in result.items() if not saved.get('cloud_only') or k not in ('ollama_installed', 'ollama_local', 'model')]
        result['ready'] = all(required)
        return result
    finally:
        os.environ['PATH'] = old

def print_checks(result):
    for name, value in result.items():
        print(('OK   ' if value else 'WAIT ') + name)
    print('SQLite is embedded in the runtime; no separate database server is required.')

def install_node(prefix, state):
    arch = 'arm64' if platform.machine().lower() in ('arm64', 'aarch64') else 'x64'
    release = next(r for r in get_json('https://nodejs.org/dist/index.json') if r.get('lts') and int(r['version'].split('.')[0][1:]) >= 22)
    stem = 'node-' + release['version'] + '-' + ('win-' if IS_WINDOWS else 'linux-') + arch
    filename = stem + ('.zip' if IS_WINDOWS else '.tar.xz')
    cache = prefix / 'downloads'; cache.mkdir(exist_ok=True)
    archive = download('https://nodejs.org/dist/' + release['version'] + '/' + filename, cache / filename)
    sums = download('https://nodejs.org/dist/' + release['version'] + '/SHASUMS256.txt', cache / 'node-shasums.txt')
    expected = next((line.split()[0] for line in sums.read_text().splitlines() if line.split()[-1] == filename), None)
    if not expected or hashlib.sha256(archive.read_bytes()).hexdigest() != expected:
        raise SetupError('Node checksum mismatch')
    tools = prefix / 'tools'; tools.mkdir(exist_ok=True)
    safe_extract(archive, tools)
    node_bin = tools / stem / ('' if IS_WINDOWS else 'bin')
    state['node_bin'] = str(node_bin)
    if not IS_WINDOWS:
        npm = node_bin / 'npm'
        npm.write_text('#!/bin/sh\nexec ' + shlex.quote(str(node_bin / 'node')) + ' ' + shlex.quote(str(tools / stem / 'lib/node_modules/npm/bin/npm-cli.js')) + ' "$@"\n')
        npm.chmod(0o755)

def install_components(prefix, state, args):
    env = environment(prefix, state); os.environ['PATH'] = env['PATH']
    cache = prefix / 'downloads'; cache.mkdir(exist_ok=True)
    if not command('git'):
        if IS_WINDOWS:
            if not command('winget'):
                raise SetupError('Install Microsoft App Installer (winget), then run setup again.')
            run(['winget', 'install', '--id', 'Git.Git', '--exact', '--source', 'winget'])
        else:
            run(['sudo', 'apt-get', 'update']); run(['sudo', 'apt-get', 'install', '-y', 'git', 'curl', 'ca-certificates', 'zstd', 'libicu-dev', 'libssl-dev', 'zlib1g'])
    if not check_sdk(env):
        if not IS_WINDOWS:
            run(['sudo', 'apt-get', 'update'])
            run(['sudo', 'apt-get', 'install', '-y', 'curl', 'ca-certificates', 'libicu-dev', 'libssl-dev', 'zlib1g'])
        script = download(URLS['dotnet'], cache / ('dotnet-install.ps1' if IS_WINDOWS else 'dotnet-install.sh'))
        run(['powershell', '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', script, '-Channel', '10.0', '-InstallDir', prefix / '.dotnet'] if IS_WINDOWS else ['bash', script, '--channel', '10.0', '--install-dir', prefix / '.dotnet'], env=env)
    if major(command('node')) < 22:
        install_node(prefix, state)
    env = environment(prefix, state); os.environ['PATH'] = env['PATH']
    if not command('docker'):
        if IS_WINDOWS:
            run(['winget', 'install', '--id', 'Docker.DockerDesktop', '--exact', '--source', 'winget'])
            print('Open Docker Desktop and finish first launch. WSL2/virtualization may require a reboot. Run this setup again afterward.')
        else:
            script = download(URLS['docker'], cache / 'docker-install.sh')
            run(['sudo', 'sh', script]); run(['sudo', 'systemctl', 'enable', '--now', 'docker'])
    if not IS_WINDOWS and args.allow_docker_group:
        run(['sudo', 'usermod', '-aG', 'docker', os.environ.get('USER') or __import__('getpass').getuser()])
        print('Docker group changed. Sign out/in before execution; setup will not run the app as root.')
    if not args.cloud_only and not command('ollama'):
        if IS_WINDOWS:
            run(['winget', 'install', '--id', 'Ollama.Ollama', '--exact', '--source', 'winget'])
        else:
            run(['sudo', 'apt-get', 'install', '-y', 'curl', 'zstd'])
            script = download(URLS['ollama'], cache / 'ollama-install.sh')
            run(['sh', script], env=env)
    env = environment(prefix, state); os.environ['PATH'] = env['PATH']
    if args.with_clis:
        node = Path(command('node')).resolve()
        npm_cli = node.parent / ('node_modules/npm/bin/npm-cli.js' if IS_WINDOWS else '../lib/node_modules/npm/bin/npm-cli.js')
        # Avoid evaluating npm.cmd through cmd.exe; use the installed npm JavaScript entrypoint.
        if not npm_cli.is_file():
            npm_path = command('npm')
            npm_cli = Path(npm_path).resolve() if npm_path and not IS_WINDOWS else npm_cli
        if not npm_cli.is_file():
            raise SetupError('npm CLI entrypoint was not found; install the official Node.js LTS distribution.')
        run([node, npm_cli, 'install', '--global', '--prefix', str(prefix / 'cli'), '@openai/codex', '@google/gemini-cli', '@github/copilot', '@anthropic-ai/claude-code'], env=env)
        print('CLI installation does not sign in. Run codex login, gemini, and copilot manually to select your own accounts.')
    return env

def check_sdk(env):
    try:
        return bool(re.search(r'^10\.', run(['dotnet', '--list-sdks'], env=env, capture=True), re.M))
    except (OSError, SetupError):
        return False

def start_ollama(prefix, state, env):
    try:
        get_json('http://127.0.0.1:11435/api/version')
        if not state.get('ollama_pid'):
            raise SetupError('Port 11435 is already occupied by a service not started by this installer. Stop that service or use the application setup guide.')
        return
    except urllib.error.URLError:
        pass
    logfile = (prefix / 'ollama.log').open('ab')
    kwargs = {'creationflags': subprocess.CREATE_NEW_PROCESS_GROUP | subprocess.DETACHED_PROCESS} if IS_WINDOWS else {'start_new_session': True}
    child = subprocess.Popen(['ollama', 'serve'], env=env, stdout=logfile, stderr=logfile, stdin=subprocess.DEVNULL, **kwargs)
    logfile.close(); state['ollama_pid'] = child.pid
    for _ in range(30):
        if child.poll() is not None:
            raise SetupError('Dedicated Ollama failed to start; inspect ollama.log.')
        try:
            get_json('http://127.0.0.1:11435/api/version'); return
        except (OSError, ValueError):
            time.sleep(1)
    raise SetupError('Ollama did not become ready; rerun setup after checking ollama.log.')

def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--prefix', type=Path, default=BASE / 'installed')
    parser.add_argument('--model', default='qwen2.5-coder:7b')
    parser.add_argument('--check', action='store_true'); parser.add_argument('--plan', action='store_true')
    parser.add_argument('--yes', action='store_true'); parser.add_argument('--with-clis', action='store_true')
    parser.add_argument('--allow-docker-group', action='store_true'); parser.add_argument('--no-model', action='store_true')
    parser.add_argument('--launch', action='store_true'); parser.add_argument('--no-browser', action='store_true')
    parser.add_argument('--source', action='store_true', help='Build the current source once, then run it with managed updates.')
    parser.add_argument('--cloud-only', action='store_true', help='Use cloud/API routes; skip Ollama and model installation.')
    parser.add_argument('--agent-shell', action='store_true', help='Open a terminal with the private Node, CLIs and Ollama environment; no login is automated.')
    args = parser.parse_args(argv); prefix = args.prefix.expanduser().resolve()
    if not IS_WINDOWS and platform.system() != 'Linux':
        raise SetupError('This bundle targets Windows and Ubuntu Linux.')
    if platform.machine().lower() not in ('amd64', 'x86_64', 'arm64', 'aarch64'):
        raise SetupError('Only x64 and arm64 are supported.')
    if not IS_WINDOWS and hasattr(os, 'geteuid') and os.geteuid() == 0 and not args.check and not args.plan:
        raise SetupError('Run setup as your normal user; individual package steps request sudo when needed.')
    state_path = prefix / 'setup-state.json'; state = json.loads(state_path.read_text()) if state_path.is_file() else {}
    if args.plan:
        print(json.dumps({'platform': platform.system(), 'prefix': str(prefix), 'components': ['Git', '.NET SDK 10', 'Node.js LTS >=22', 'Docker local Linux', 'Ollama dedicated port 11435 with cloud disabled', 'SQLite embedded', 'node:22-alpine', args.model, 'managed demo project'], 'optional_clis': args.with_clis, 'vendor_scripts': URLS}, indent=2)); return 0
    if args.check:
        result = check(prefix, state); print_checks(result); return 0 if result['ready'] else 10
    if args.agent_shell:
        env = environment(prefix, state)
        print('Your own account logins: codex login | gemini | copilot (then /login). Ollama endpoint: 127.0.0.1:11435. Exit to close this terminal.')
        return subprocess.call(['powershell', '-NoProfile', '-NoExit'] if IS_WINDOWS else ['/bin/bash', '--noprofile', '--norc'], env=env, cwd=BASE)
    if args.source:
        source = BASE.parent
        if not (source / 'src/AICompanyOS.Runtime/AICompanyOS.Runtime.csproj').is_file():
            raise SetupError('--source requires a source checkout.')
        prefix.mkdir(parents=True, exist_ok=True)
        env = environment(prefix, state)
        if not check_sdk(env): env = install_components(prefix, state, args)
        run(['dotnet','publish',source / 'src/AICompanyOS.Runtime','-c','Release','--self-contained','false','-o',BASE / 'app'], env=env)
        run([sys.executable, BASE / 'build-bundle.py','--runtime',BASE / 'app','--output',prefix / 'source-build.zip'], env=env)
        with zipfile.ZipFile(prefix / 'source-build.zip') as bundle:
            (BASE / 'SHA256SUMS.json').write_bytes(bundle.read('SHA256SUMS.json'))
        (prefix / 'updates/active.json').unlink(missing_ok=True)
        run(['dotnet', str(BASE / 'app/AICompanyOS.Runtime.dll'), '--initialize-local'], env=env, cwd=BASE / 'app')
    if not MODEL_PATTERN.fullmatch(args.model) or 'cloud' in args.model.lower():
        raise SetupError('Use a local GGUF model name (maximum 120 characters); cloud aliases are not supported.')
    if not (BASE / 'app/AICompanyOS.Runtime.dll').is_file():
        raise SetupError('Runtime app/ is missing. Use the complete setup ZIP or build it with setup/build-bundle.py.')
    verify_bundle()
    if not args.yes and not args.launch:
        print('Installs missing components from vendor sources. Docker may need admin/WSL2/relogin. SQLite and demo are local. Model download is approximately 4.7 GB for qwen2.5-coder:7b; model/hardware quality requires a real run.')
        if input('Continue installation? [y/N] ').strip().lower() != 'y':
            return 1
    prefix.mkdir(parents=True, exist_ok=True)
    try:
        env = environment(prefix, state) if args.launch else install_components(prefix, state, args)
        os.environ['PATH'] = env['PATH']
        if args.cloud_only: state['cloud_only'] = True
        if not state.get('cloud_only'): start_ollama(prefix, state, env)
        if not args.launch and not args.no_model and not state.get('cloud_only'):
            run(['ollama', 'pull', args.model], env=env); state['model'] = args.model
        if not args.launch:
            try:
                run(docker_args() + ['image', 'inspect', 'node:22-alpine'], env=env, capture=True)
            except (SetupError, OSError):
                try: run(docker_args() + ['pull', 'node:22-alpine'], env=env)
                except (SetupError, OSError): print('WAIT Docker/image. Start Docker Desktop or sign out/in after a Docker group change, then run setup again.')
            app = update_engine.current_app(BASE, prefix)
            initialize = ['dotnet', str(app / 'AICompanyOS.Runtime.dll'), '--initialize-local']
            if state.get('model') and not state.get('cloud_only'):
                initialize += ['--model', state['model']]
            run(initialize, env=env, cwd=app)
        state_path.write_text(json.dumps(state, indent=2))
        result = check(prefix, state); print_checks(result)
        if not result['ready'] and not args.launch:
            print('Setup is incomplete; WAIT items are not treated as PASS. Rerun after completing them. --no-model creates the demo for manual typed output, not automatic AI.')
            return 10
        print('READY: http://127.0.0.1:4173 — open Demo · Agent Company, review and start the demo.')
        if args.launch or not args.no_browser:
            if not args.no_browser:
                import threading
                threading.Timer(2, lambda: webbrowser.open('http://127.0.0.1:4173')).start()
            return update_engine.supervise(BASE, prefix, env)
        return 0
    finally:
        state_path.write_text(json.dumps(state, indent=2))

if __name__ == '__main__':
    try:
        sys.exit(main())
    except (SetupError, OSError, ValueError) as error:
        print('SETUP STOPPED:', error, file=sys.stderr)
        sys.exit(1)
