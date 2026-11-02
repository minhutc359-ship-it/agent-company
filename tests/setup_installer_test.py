"""Installer safety/portability checks; no vendor install, account or network access."""
import contextlib
import hashlib
import importlib.util
import io
import json
import os
from pathlib import Path
import sqlite3
import tarfile
import tempfile
import unittest
from unittest.mock import patch
import zipfile

spec = importlib.util.spec_from_file_location('setup_aicos', Path(__file__).resolve().parents[1] / 'setup/setup-aicos.py')
setup = importlib.util.module_from_spec(spec)
import sys
sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'setup'))
spec.loader.exec_module(setup)

class InstallerTests(unittest.TestCase):
    def test_plan_does_not_install_or_write(self):
        with tempfile.TemporaryDirectory() as folder, patch.object(setup, 'run', side_effect=AssertionError('unexpected process')), patch.object(setup, 'download', side_effect=AssertionError('unexpected network')):
            prefix = Path(folder) / 'not-created'
            with contextlib.redirect_stdout(io.StringIO()) as output:
                self.assertEqual(setup.main(['--prefix', str(prefix), '--plan']), 0)
            self.assertFalse(prefix.exists())
            self.assertIn('SQLite embedded', output.getvalue())

    def test_check_does_not_start_or_install(self):
        with tempfile.TemporaryDirectory() as folder, patch.object(setup, 'command', return_value=None), patch.object(setup, 'run', side_effect=setup.SetupError('missing')), patch.object(setup, 'get_json', side_effect=OSError('offline')), patch.object(setup, 'start_ollama', side_effect=AssertionError('unexpected start')), patch.object(setup, 'install_components', side_effect=AssertionError('unexpected install')):
            prefix = Path(folder) / 'not-created'
            with contextlib.redirect_stdout(io.StringIO()) as output:
                self.assertEqual(setup.main(['--prefix', str(prefix), '--check']), 10)
            self.assertFalse(prefix.exists())
            self.assertIn('WAIT model', output.getvalue())

    def test_archive_traversal_is_rejected(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            for name in ['../outside', '/absolute', 'C:/outside', 'inside\\..\\outside']:
                archive = root / 'bad.zip'
                with zipfile.ZipFile(archive, 'w') as z:
                    z.writestr(name, 'bad')
                with self.assertRaises(setup.SetupError):
                    setup.safe_extract(archive, root / 'out')
            self.assertFalse((root / 'outside').exists())

    def test_zip_links_are_rejected_and_tar_links_skipped(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder); archive = root / 'bad.zip'
            with zipfile.ZipFile(archive, 'w') as z:
                entry = zipfile.ZipInfo('link'); entry.external_attr = 0o120777 << 16; z.writestr(entry, '/tmp/outside')
            with self.assertRaises(setup.SetupError):
                setup.safe_extract(archive, root / 'out')
            archive = root / 'node.tar'
            with tarfile.open(archive, 'w') as t:
                link = tarfile.TarInfo('node/link'); link.type = tarfile.SYMTYPE; link.linkname = '/outside'; t.addfile(link)
                item = tarfile.TarInfo('node/node'); item.size = 2; t.addfile(item, io.BytesIO(b'ok'))
            setup.safe_extract(archive, root / 'out')
            self.assertFalse((root / 'out/node/link').exists())
            self.assertEqual((root / 'out/node/node').read_bytes(), b'ok')

    def test_bundle_detects_missing_corrupt_and_outside_files(self):
        with tempfile.TemporaryDirectory() as folder, patch.object(setup, 'BASE', Path(folder)):
            root = Path(folder); (root / 'app').mkdir()
            files = {'setup-aicos.py': b'script', 'app/AICompanyOS.Runtime.dll': b'runtime'}
            for name, content in files.items(): (root / name).write_bytes(content)
            manifest = {name: hashlib.sha256(content).hexdigest() for name, content in files.items()}
            (root / 'SHA256SUMS.json').write_text(json.dumps(manifest)); setup.verify_bundle()
            (root / 'app/AICompanyOS.Runtime.dll').write_bytes(b'corrupt')
            with self.assertRaises(setup.SetupError): setup.verify_bundle()
            manifest['../outside'] = '0' * 64
            (root / 'SHA256SUMS.json').write_text(json.dumps(manifest))
            with self.assertRaises(setup.SetupError): setup.verify_bundle()

    def test_sqlite_is_real_readonly_database(self):
        with tempfile.TemporaryDirectory() as folder:
            prefix = Path(folder); (prefix / 'data').mkdir()
            (prefix / 'data/storage.json').write_text('{"backend":"sqlite"}')
            self.assertFalse(setup.check_sqlite(prefix))
            with contextlib.closing(sqlite3.connect(prefix / 'data/events.sqlite')) as db:
                with db:
                    db.execute('CREATE TABLE events (body TEXT)'); db.execute("INSERT INTO events VALUES ('{}')")
            self.assertTrue(setup.check_sqlite(prefix))
            (prefix / 'data/storage.json').write_text('{"backend":"sqlite","generation":"../../outside"}')
            self.assertFalse(setup.check_sqlite(prefix))

    def test_private_environment_and_local_docker(self):
        with tempfile.TemporaryDirectory() as folder, patch.object(setup, 'IS_WINDOWS', True):
            prefix = Path(folder); env = setup.environment(prefix, {'node_bin': str(prefix / 'tools/node')})
            self.assertIn(str(prefix / 'cli'), env['PATH'].split(os.pathsep))
            self.assertEqual(env['OLLAMA_HOST'], '127.0.0.1:11435')
            self.assertEqual(env['OLLAMA_NO_CLOUD'], '1')
            self.assertEqual(setup.docker_args(), ['docker', '--host', 'npipe:////./pipe/docker_engine'])
        with patch.object(setup, 'IS_WINDOWS', False):
            self.assertEqual(setup.docker_args(), ['docker', '--host', 'unix:///var/run/docker.sock'])

    def test_download_origin_rejected_before_network(self):
        with patch.object(setup.urllib.request, 'urlopen', side_effect=AssertionError('unexpected network')):
            for url in ['http://nodejs.org/file', 'https://evil.invalid/file', 'file:///etc/passwd']:
                with self.assertRaises(setup.SetupError): setup.download(url, 'unused')

if __name__ == '__main__':
    unittest.main()
