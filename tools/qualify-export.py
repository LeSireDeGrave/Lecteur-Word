#!/usr/bin/env python3
"""One explicitly selected exporter qualification, using generated fixtures only."""
import argparse,hashlib,json,pathlib,subprocess,tempfile,zipfile,xml.etree.ElementTree as ET,re,os
p=argparse.ArgumentParser();p.add_argument('--format',required=True,choices=['pdf','odt','epub']);a=p.parse_args()
root=pathlib.Path(__file__).resolve().parents[1];logs=root/'.local/qualification'
items=[json.loads(l) for l in (logs/'targeted.jsonl').read_text().splitlines()]
item=next(i for i in reversed(items) if i['case']=='export-'+a.format and i['exitCode']==0)
fixture=pathlib.Path(item['stdout'].strip().split('fixtures: ')[-1]);path=fixture/'export'/('document.'+a.format)
work=pathlib.Path(tempfile.mkdtemp(prefix='lecteur-export-qa-'));records=[]
before=hashlib.file_digest(path.open('rb'),'sha256').hexdigest()
def run(command):
 env=dict(os.environ,GSETTINGS_BACKEND='memory',SAL_USE_VCLPLUGIN='svp')
 if str(command[0])=='libreoffice':
  runtime=work/'runtime';runtime.mkdir(mode=0o700,exist_ok=True);env['XDG_RUNTIME_DIR']=str(runtime);env.pop('DBUS_SESSION_BUS_ADDRESS',None)
 r=subprocess.run([str(x) for x in command],capture_output=True,text=True,timeout=60,env=env)
 record=dict(command=[str(x) for x in command],exitCode=r.returncode,stdout=r.stdout,stderr=r.stderr);records.append(record)
 assert r.returncode==0,record
 return r
if a.format=='pdf':
 info=run(['pdfinfo',path]);assert re.search(r'Pages:\s+1',info.stdout),info.stdout
 fonts=run(['pdffonts',path]);assert 'DejaVuSerif' in fonts.stdout and re.search(r'yes\s+yes\s+yes',fonts.stdout),fonts.stdout
 text=work/'extracted.txt';run(['pdftotext','-layout',path,text])
 expected=json.loads((fixture/'expected.json').read_text())
 assert ''.join(''.join(expected).split())==''.join(text.read_text().split()),(expected,text.read_text())
 run(['mutool','draw','-q','-r','96','-o',work/'pdf.png',path])
elif a.format=='odt':
 with zipfile.ZipFile(path) as z:
  for name in ['content.xml','styles.xml','META-INF/manifest.xml']:
   target=work/pathlib.Path(name).name;target.write_bytes(z.read(name));schema='odf-manifest.rng' if 'manifest' in name else 'odf.rng'
   run(['xmllint','--nonet','--noout','--relaxng',root/'.local/qa'/schema,target])
 out=work/'reopened';out.mkdir()
 profile=(work/'lo-profile').as_uri()
 run(['libreoffice','-env:UserInstallation='+profile,'--headless','--convert-to','odt','--outdir',out,path])
 assert (out/'document.odt').exists(),'No QA reopening result'
 with zipfile.ZipFile(out/'document.odt') as z:
  t='{urn:oasis:names:tc:opendocument:xmlns:text:1.0}'
  def read(e):
   value=e.text or ''
   for c in e:
    value+=((' '*int(c.attrib.get(t+'c',1))) if c.tag==t+'s' else '\t' if c.tag==t+'tab' else '\n' if c.tag==t+'line-break' else read(c))+(c.tail or '')
   return value
  actual=[read(e) for e in ET.fromstring(z.read('content.xml')).iter(t+'p')]
  expected=json.loads((fixture/'expected.json').read_text());assert actual==expected,(actual,expected)
else:
 run(['java','-jar',root/'.local/qa/epubcheck-5.3.0/epubcheck.jar',path])
assert hashlib.file_digest(path.open('rb'),'sha256').hexdigest()==before,'QA modified original export'
report=dict(format=a.format,source=str(path),sha256=before,work=str(work),checks=records,profile='plain-paragraphs-v1',status='OK limited profile')
(logs/('qualified-'+a.format+'.json')).write_text(json.dumps(report,indent=2));print(json.dumps(report,indent=2))
