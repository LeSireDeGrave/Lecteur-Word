#!/usr/bin/env python3
"""Review an explicit initial-commit candidate. Never stages, commits, pushes or reads other repos."""
import argparse,hashlib,json,pathlib,re,struct,subprocess,sys,xml.etree.ElementTree as ET
parser=argparse.ArgumentParser();parser.add_argument('--staged',action='store_true');args=parser.parse_args()
root=pathlib.Path(__file__).resolve().parents[1];out=root/'.local/qualification/github-002';out.mkdir(parents=True,exist_ok=True)
expected_origin='https://github.com/LeSireDeGrave/Lecteur-Word.git'
def git(*argv,ok=(0,)):
 r=subprocess.run(['git',*argv],cwd=root,capture_output=True)
 if r.returncode not in ok:raise RuntimeError(r.stderr.decode(errors='replace'))
 return r
files=['.gitignore','AGENTS.md','Directory.Build.props','LecteurWord.slnx','NuGet.Config','README.md','global.json']
groups={
 'src/DocxCore':['AtomicOutput.cs','CompoundClassifier.cs','Contract.cs','DocxCore.csproj','InputFile.cs','Inspector.cs','ZipPreflight.cs','packages.lock.json'],
 'src/DocxCore.Cli':['DocxCore.Cli.csproj','Program.cs','packages.lock.json'],
 'src/DocxFlow':['Composer.cs','DocxFlow.csproj','Exporters.cs','FlowModel.cs','QuotaStream.cs','packages.lock.json','Fonts/DejaVuSerif.ttf','Fonts/LICENSE-DejaVu.txt'],
 'src/LecteurWord':['Icons.cs','LecteurWord.csproj','Program.cs','ReaderApp.cs','ReaderWindow.cs','packages.lock.json'],
 'tests/DocxCore.Targeted':['DocxCore.Targeted.csproj','Fixtures.cs','Program.cs','packages.lock.json'],
 'tests/Viewer.Targeted':['Viewer.Targeted.csproj','Program.cs','packages.lock.json'],
 'tests/contract-rust':['Cargo.toml','Cargo.lock','src/main.rs'],
 'tools':['bootstrap-dotnet.py','check-contract.py','check-schema.py','check-target.py','license-inventory.py','qualify-cli.py','qualify-export.py','review-first-commit.py','run-isolated.py','write-schema.py'],
 'docs':['architecture-et-couverture.md','contrat-opusnova-v1.md','dependances.md','manifest-v1.schema.json','provenance.md','securite.md','ux.md','maquettes/README.md','maquettes/lecteur-word-maquette-clair-sombre.png']}
files=sorted(files+[folder+'/'+name for folder,names in groups.items() for name in names]);allowed=set(files)
assets={'docs/maquettes/lecteur-word-maquette-clair-sombre.png':'079276d8a88699c393622cd1500c1b753c6dc47cbcc03d75d00f4948105aa177','src/DocxFlow/Fonts/DejaVuSerif.ttf':'a52c2a69dfc93d0b511b98c1019a57eb3d6a62d328bdf1387f00851b02e3a315'}
checks={
 'private_key':re.compile(r'-----BEGIN (?:RSA |EC |OPENSSH |DSA |ENCRYPTED )?PRIVATE KEY-----'),
 'github_token':re.compile(r'\b(?:gh[pousr]_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,})'),
 'aws_access_key':re.compile(r'\bAKIA[0-9A-Z]{16}\b'),
 'personal_absolute_path':re.compile(r'/(?:home|Users)/[^/\s]+/|[A-Za-z]:\\Users\\[^\\\s]+\\'),
 'credential_url':re.compile(r'https?://[^\s/:@]+:[^\s/@]+@')}
assert git('remote','get-url','--all','origin').stdout.decode().splitlines()==[expected_origin]
assert git('remote','get-url','--push','--all','origin').stdout.decode().splitlines()==[expected_origin]
assert git('symbolic-ref','--short','HEAD').stdout.decode().strip()=='main'
assert git('rev-parse','--verify','HEAD',ok=(0,128)).returncode==128,'Commit unexpectedly exists'
rows=[]
for name in files:
 path=root/name;assert path.is_file() and not path.is_symlink(),name
 assert path.resolve().is_relative_to(root) and path.resolve()==path,name
 assert git('check-ignore','--no-index','-q','--',name,ok=(0,1)).returncode==1,'Ignored candidate: '+name
 data=path.read_bytes();digest=hashlib.sha256(data).hexdigest()
 if name in assets:assert digest==assets[name],name+' asset changed'
 else:
  text=data.decode('utf-8');assert '\0' not in text,name+' contains binary data'
  for label,pattern in checks.items():assert not pattern.search(text),name+' flagged: '+label
  diff=git('diff','--no-index','--check','--','/dev/null',name,ok=(0,1,2,3))
  assert not diff.stdout and not diff.stderr and diff.returncode in (0,1),name+' whitespace error'
 rows.append(dict(path=name,bytes=len(data),sha256=digest,kind='approved source asset' if name in assets else 'source/config/documentation'))
# A build from the candidate must retain project references, compiled sources and embedded assets.
for name in files:
 if name.endswith('.csproj'):
  project=root/name;tree=ET.parse(project)
  for e in tree.getroot().iter():
   if e.tag in ['ProjectReference','EmbeddedResource','Compile'] and 'Include' in e.attrib:
    target=(project.parent/e.attrib['Include']).resolve().relative_to(root).as_posix();assert target in allowed,'Missing build input: '+target
  for source in project.parent.rglob('*.cs'):
   if any(p in ['bin','obj','target'] for p in source.relative_to(root).parts):continue
   assert source.relative_to(root).as_posix() in allowed,'Unreviewed compiled source: '+str(source)
for e in ET.parse(root/'LecteurWord.slnx').getroot().iter('Project'):assert e.attrib['Path'] in allowed
# Only the explicitly requested image may appear in README image references.
readme=(root/'README.md').read_text();links=re.findall(r'\]\(([^)]+)\)',readme)
for link in links:
 if '://' not in link and not link.startswith('#'):assert (root/link.split('#')[0]).exists(),'Broken README link: '+link
assert '](docs/maquettes/lecteur-word-maquette-clair-sombre.png)' in readme
# Representative private/generated files stay ignored; automated test source remains reviewable.
probes=['.local/private.txt','src/LecteurWord/bin/Release/net10.0/LecteurWord.dll','tests/Viewer.Targeted/obj/project.assets.json','TestResults/results.trx','corpus/manuscrit.docx','captures/fenetre.png','.env.local','.aws/credentials','.codex/settings.json','private.pdf','package.zip','tools/verify-workspace.py','tools/write-final-report.py']
for name in probes:assert git('check-ignore','--no-index','-q','--',name,ok=(0,1)).returncode==0,'Unprotected generated/private path: '+name
if args.staged:
 staged=git('diff','--cached','--name-only','-z').stdout.decode().rstrip('\0').split('\0');assert set(staged)==allowed,'Index does not match reviewed list'
 assert not git('diff','--cached','--check').stdout
 for row in rows:
  data=git('show',':'+row['path']).stdout;assert hashlib.sha256(data).hexdigest()==row['sha256'],'Stale staged file: '+row['path']
else:assert not git('ls-files','--stage').stdout,'Index was not empty before preparation'
report=dict(stage='staged review' if args.staged else 'candidate review',origin=expected_origin,branch='main',noCommit=True,fileCount=len(rows),totalBytes=sum(r['bytes'] for r in rows),assetsUnchanged=True,readmeLinksChecked=True,privacyAndWhitespace='OK on reviewed inputs',exclusionProbes=len(probes),files=rows)
(out/('staged-review.json' if args.staged else 'candidate-review.json')).write_text(json.dumps(report,indent=2)+'\n')
(out/'candidate.paths').write_bytes(b'\0'.join(p.encode() for p in files)+b'\0')
print('OK',report['stage'],':',len(rows),'files,',report['totalBytes'],'bytes; no commit or push')
