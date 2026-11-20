#!/usr/bin/env python3
"""Rust interoperability checks against a synthetic C# manifest; never opens .nova."""
import json,pathlib,subprocess,tempfile
root=pathlib.Path(__file__).resolve().parents[1];logs=root/'.local/qualification'
work=pathlib.Path(tempfile.mkdtemp(prefix='lecteur-contract-'))
items=[json.loads(l) for l in (logs/'targeted.jsonl').read_text().splitlines()]
fixture=next(i for i in reversed(items) if i['case']=='images' and i['exitCode']==0)['stdout'].strip().split('fixtures: ')[-1]
source=pathlib.Path(fixture)/'images.docx'
r=subprocess.run([str(root/'.local/publish/docx-core'),'inspect','--input',str(source),'--output',str(work/'inspection')],capture_output=True,text=True)
assert r.returncode==2,(r.returncode,r.stdout,r.stderr)
manifest=json.loads((work/'inspection/manifest.json').read_text())
exe=root/'.local/rust-target/debug/lecteur-word-contract-probe';records=[]
for name,mutation,expected in [('version-1',lambda m:None,0),('version-2',lambda m:m.update(schemaVersion=2),65),('import-ready',lambda m:m.update(importReady=True),65),('asset-path',lambda m:m['media'][0].update(id='../escape'),65),('missing-parent',lambda m:m['nodes'][1].update(parentId='n999999'),65)]:
 value=json.loads(json.dumps(manifest));mutation(value);path=work/(name+'.json');path.write_text(json.dumps(value))
 cmd=[str(exe),str(path)];r=subprocess.run(cmd,capture_output=True,text=True,timeout=5)
 records.append(dict(case=name,command=cmd,exitCode=r.returncode,stdout=r.stdout,stderr=r.stderr));assert r.returncode==expected,records[-1]
(logs/'rust-contract.json').write_text(json.dumps(records,indent=2));print('OK Rust contract: 1 accepted, 4 refused; no .nova writes')
