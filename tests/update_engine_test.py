import hashlib
import io
import json
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch
import zipfile

sys.path.insert(0,str(Path(__file__).resolve().parents[1]/'setup'))
import update_engine as update

def bundle(number=2, extra=None):
    files={'app/AICompanyOS.Runtime.dll':b'fixture-runtime-not-executable', 'app/build-info.json':json.dumps({'version':'2.5.0','commit':'b'*40,'buildNumber':number,'protocol':1,'runtimeMajor':10}).encode()}
    if extra: files.update(extra)
    data=io.BytesIO()
    with zipfile.ZipFile(data,'w') as z:
        for name,content in files.items():z.writestr(name,content)
        z.writestr('SHA256SUMS.json',json.dumps({n:hashlib.sha256(c).hexdigest() for n,c in files.items()}))
    return data.getvalue()

class UpdateTests(unittest.TestCase):
    def setUp(self):
        self.temp=tempfile.TemporaryDirectory();self.root=Path(self.temp.name).resolve();self.prefix=self.root/'installed';self.app=self.root/'app';self.app.mkdir()
        (self.app/'build-info.json').write_text(json.dumps({'version':'2.5.0','commit':'a'*40,'buildNumber':1}))
        (self.prefix/'data').mkdir(parents=True);(self.prefix/'data/events.sqlite').write_bytes(b'original-closed-db')
        (self.prefix/'models').mkdir();(self.prefix/'models/weights').write_bytes(b'untouched-model')
        self.raw=bundle();self.url='https://github.com/'+update.REPOSITORY+'/releases/download/build-2/'+update.BUNDLE
    def tearDown(self):self.temp.cleanup()
    def release(self):return {'tag_name':'build-2','assets':[{'name':update.BUNDLE,'size':len(self.raw),'digest':'sha256:'+hashlib.sha256(self.raw).hexdigest(),'browser_download_url':self.url}]}
    def reader(self,url,limit=0):return json.dumps(self.release()).encode() if url==update.API else self.raw
    def checked(self):
        candidate=update.latest(self.app,self.reader);update.atomic_json(self.prefix/'updates/state.json',{**candidate,'phase':'checked'});return candidate
    def test_check_newer_and_never_downgrade(self):
        self.assertTrue(self.checked()['available'])
        (self.app/'build-info.json').write_text('{"buildNumber":3}')
        self.assertFalse(update.latest(self.app,self.reader)['available'])
    def test_only_fixed_https_repository_and_asset_hosts(self):
        for url in ['http://github.com/x','https://github.com/other/repo/releases/download/x/file','https://evil.invalid/file','https://user@api.github.com/repos/'+update.REPOSITORY+'/releases/latest']:
            with self.assertRaises(update.UpdateError):update.validate_url(url)
    def test_draft_or_unchecksummed_release_rejected(self):
        for change in [{'draft':True},{'prerelease':True},{'tag_name':'../../escape'},{'assets':[]}]:
            with self.assertRaises(update.UpdateError):update.latest(self.app,lambda _:json.dumps({**self.release(),**change}).encode())
        bad=self.release();bad['assets'][0].pop('digest')
        with self.assertRaises(update.UpdateError):update.latest(self.app,lambda _:json.dumps(bad).encode())
    def test_stage_does_not_touch_live_data_or_models(self):
        self.checked();result=update.stage(self.root,self.prefix,self.reader)
        self.assertEqual(result['phase'],'ready');self.assertFalse((self.prefix/'updates/active.json').exists())
        self.assertEqual((self.prefix/'data/events.sqlite').read_bytes(),b'original-closed-db')
        self.assertEqual((self.prefix/'models/weights').read_bytes(),b'untouched-model')
    def test_changed_release_needs_new_check(self):
        self.checked();self.raw=bundle(3)
        with self.assertRaises(update.UpdateError):update.stage(self.root,self.prefix,self.reader)
    def test_corrupt_download_rejected_without_activation(self):
        self.checked()
        def reader(url,limit=0):return self.reader(url) if url==update.API else b'corrupt'
        with self.assertRaises(update.UpdateError):update.stage(self.root,self.prefix,reader)
        self.assertEqual(update.read_json(self.prefix/'updates/state.json')['phase'],'failed')
        self.assertEqual((self.prefix/'data/events.sqlite').read_bytes(),b'original-closed-db')
    def test_archive_traversal_extra_file_and_tamper_rejected(self):
        for extra in [{'../escape':b'x'},{'/absolute':b'x'},{'app\\escape':b'x'},{'app/CON.txt':b'x'},{'APP/aiCompanyOS.Runtime.dll':b'x'}]:
            path=self.root/'test.zip';path.write_bytes(bundle(extra=extra))
            with self.assertRaises(update.UpdateError):update.extract_verified(path,self.root/'staging')
        path=self.root/'test.zip';path.write_bytes(self.raw)
        with zipfile.ZipFile(path,'a') as z:z.writestr('unlisted.txt','not in manifest')
        with self.assertRaises(update.UpdateError):update.extract_verified(path,self.root/'other-staging')
    def test_activate_rechecks_and_keeps_backup(self):
        self.checked();update.stage(self.root,self.prefix,self.reader)
        (self.prefix/'updates/versions/build-2/app/AICompanyOS.Runtime.dll').write_bytes(b'tampered')
        with self.assertRaises(update.UpdateError):update.activate(self.prefix)
        self.assertFalse((self.prefix/'updates/active.json').exists())
    def test_rollback_restores_application_pointer_and_database(self):
        self.checked();update.stage(self.root,self.prefix,self.reader);update.activate(self.prefix)
        (self.prefix/'data/events.sqlite').write_bytes(b'new-schema-data')
        update.rollback(self.prefix)
        self.assertEqual(update.current_app(self.root,self.prefix),self.app)
        self.assertEqual((self.prefix/'data/events.sqlite').read_bytes(),b'original-closed-db')
        self.assertEqual((self.prefix/'models/weights').read_bytes(),b'untouched-model')
    def test_supervisor_rolls_back_failed_health_and_restarts_old_app(self):
        self.checked();update.stage(self.root,self.prefix,self.reader)
        update.atomic_json(self.prefix/'updates/apply-request.json',{})
        class Process:
            def __init__(self,code):self.code=code;self.stopped=False
            def wait(self,timeout=None):return self.code
            def poll(self):return None
            def terminate(self):self.stopped=True
            def kill(self):self.stopped=True
        old=Process(42);new=Process(1);restored=Process(0)
        def failure(expected,process):
            (self.prefix/'data/events.sqlite').write_bytes(b'partial-migration');return False
        with patch.object(update.subprocess,'Popen',side_effect=[old,new,restored]) as launch:
            self.assertEqual(update.supervise(self.root,self.prefix,{},probe=failure),0)
            self.assertEqual(launch.call_count,3)
        self.assertTrue(new.stopped)
        self.assertEqual((self.prefix/'data/events.sqlite').read_bytes(),b'original-closed-db')
        self.assertEqual(update.read_json(self.prefix/'updates/state.json')['phase'],'rolled_back')
    def test_successful_supervisor_retains_new_version_and_clears_recovery_journal(self):
        self.checked();update.stage(self.root,self.prefix,self.reader);update.atomic_json(self.prefix/'updates/apply-request.json',{})
        class Process:
            def __init__(self,code):self.code=code
            def wait(self,timeout=None):return self.code
        with patch.object(update.subprocess,'Popen',side_effect=[Process(42),Process(0)]):
            self.assertEqual(update.supervise(self.root,self.prefix,{},probe=lambda *_:True),0)
        self.assertEqual(update.current_app(self.root,self.prefix),self.prefix/'updates/versions/build-2/app')
        self.assertFalse((self.prefix/'updates/transaction.json').exists())

if __name__=='__main__':unittest.main()
