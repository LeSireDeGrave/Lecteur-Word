#!/usr/bin/env python3
"""Exact managed graph/SPDX inventory, with native-notice and publication limits explicit."""
import hashlib,json,pathlib,xml.etree.ElementTree as ET,tomllib
root=pathlib.Path(__file__).resolve().parents[1];items={}
for component in ['DocxCore','DocxFlow','LecteurWord']:
 lock=json.loads((root/'src'/component/'packages.lock.json').read_text())
 for name,item in lock['dependencies']['net10.0'].items():
  if 'resolved' not in item:continue
  version=item['resolved'];key=(name,version)
  if key in items:items[key]['components'].append(component);continue
  folder=root/'.local/packages'/name.lower()/version
  metadata=ET.parse(folder/(name.lower()+'.nuspec')).getroot().find('{*}metadata');license=metadata.find('{*}license')
  info=dict(name=name,version=version,components=[component],nugetContentHash=item['contentHash'])
  if license is None:info.update(spdx='NOASSERTION',notice='Review upstream metadata')
  elif license.attrib['type']=='expression':info.update(spdx=license.text)
  else:
   path=folder/license.text;info.update(spdx='LicenseRef-'+name.replace('.','-')+'-Bundled',notice=str(path),noticeSha256=hashlib.sha256(path.read_bytes()).hexdigest())
  items[key]=info
rust=[]
for item in tomllib.loads((root/'tests/contract-rust/Cargo.lock').read_text())['package']:
 if 'source' not in item:continue
 matches=list((pathlib.Path.home()/'.cargo/registry/src').glob('*/'+item['name']+'-'+item['version']+'/Cargo.toml'))
 metadata=tomllib.loads(matches[0].read_text())['package'] if matches else {}
 rust.append(dict(name=item['name'],version=item['version'],spdx=metadata.get('license','NOASSERTION'),checksum=item.get('checksum')))
report=dict(newCodeLicense='LicenseRef-Pending-Owner-Decision',dependencies=list(items.values()),
 runtime=dict(name='Microsoft.NETCore.App.Runtime.linux-x64',version='10.0.15',spdx='MIT',notices=str(root/'.local/packages/microsoft.netcore.app.runtime.linux-x64/10.0.15/THIRD-PARTY-NOTICES.TXT')),
 fonts=[dict(name='DejaVu Serif',version='2.37',spdx='Bitstream-Vera AND LicenseRef-DejaVu-Public-Domain',sha256=hashlib.sha256((root/'src/DocxFlow/Fonts/DejaVuSerif.ttf').read_bytes()).hexdigest(),notice='src/DocxFlow/Fonts/LICENSE-DejaVu.txt'),dict(name='Inter',spdx='OFL-1.1',packaging='Avalonia.Fonts.Inter; preserve upstream font notices before distribution')],
 rustProbe=dict(role='QA only',dependencies=rust),
 note='NuGet graph includes optional non-Linux native assets. Wrapper MIT does not replace embedded/native third-party notices. OS dynamic libraries, Skia/HarfBuzz notices, Inter notices and the new-code licence remain publication review obligations. No public distribution authorized.')
out=root/'.local/qualification/licenses.json';out.write_text(json.dumps(report,indent=2)+'\n');print('Inventoried',len(items),'exact NuGet package versions, runtime and fonts')
