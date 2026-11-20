#!/usr/bin/env python3
"""Explicitly selected CLI checks and measurements for DOCX-CORE-001A only."""
import argparse, hashlib, json, os, pathlib, signal, subprocess, tempfile, time
p=argparse.ArgumentParser();p.add_argument('--case',required=True,choices=['cli','network','resources','measurements','packaging'])
a=p.parse_args();root=pathlib.Path(__file__).resolve().parents[1]
logs=root/'.local/qualification';binary=root/'.local/publish/docx-core'
work=pathlib.Path(tempfile.mkdtemp(prefix='docx-core-cli-001a-'))
records=[]
def fixture(case):
    items=[json.loads(l) for l in (logs/'targeted.jsonl').read_text().splitlines()]
    item=next(i for i in reversed(items) if i['case']==case and i['exitCode']==0)
    if case.startswith('measure-'):return pathlib.Path(json.loads(item['stdout'].splitlines()[0])['input'])
    location=item['stdout'].strip().split('fixtures: ')[-1]
    return pathlib.Path(location)
def sha(path):return hashlib.file_digest(open(path,'rb'),'sha256').hexdigest()
def run(command,expected):
    r=subprocess.run([str(x) for x in command],capture_output=True,text=True,timeout=60,env=dict(os.environ,DOTNET_CLI_TELEMETRY_OPTOUT='1'))
    records.append(dict(command=[str(x) for x in command],exitCode=r.returncode,stdout=r.stdout,stderr=r.stderr))
    assert r.returncode==expected, records[-1]
    return r
small=fixture('measure-small');large=fixture('measure-large')
if a.case=='cli':
    result=run([binary,'capabilities'],0);assert json.loads(result.stdout)['schemaVersions']==[1]
    run([binary],64)
    run([binary,'inspect','--input',small,'--output',work/'v2','--schema-version','2'],65)
    before=sha(small)
    run([binary,'inspect','--input',small,'--output',work/'good'],2)
    manifest=json.loads((work/'good/manifest.json').read_text());assert manifest['inspectionComplete'] and not manifest['importReady']
    run([binary,'inspect','--input',small,'--output',small],74);assert sha(small)==before
    run([binary,'inspect','--input',small,'--output',work/'good'],74)
    bad=work/'fake.docx';bad.write_text('not a docx');run([binary,'inspect','--input',bad,'--output',work/'bad'],3)
    assert not json.loads((work/'bad/manifest.json').read_text())['inspectionComplete']
    options=work/'options.json';options.write_text('{"maxDurationMilliseconds":1}')
    run([binary,'inspect','--input',large,'--output',work/'limited','--options',options],75)
    options.write_text('{"maxDepth":0}');run([binary,'inspect','--input',small,'--output',work/'invalid-options','--options',options],64)
    options.write_text('{"unrecognizedBudget":1}');run([binary,'inspect','--input',small,'--output',work/'unknown-option','--options',options],64)
    options.write_text('{invalid');run([binary,'inspect','--input',small,'--output',work/'invalid-json','--options',options],64)
    fifo=work/'hostile.docx';os.mkfifo(fifo)
    start=time.monotonic();run([binary,'inspect','--input',fifo,'--output',work/'fifo-result'],74);assert time.monotonic()-start<2
    link=work/'symlink.docx';link.symlink_to(small);run([binary,'inspect','--input',link,'--output',work/'link-result'],74)
    before=sha(large)
    child=subprocess.Popen([str(binary),'inspect','--input',str(large),'--output',str(work/'cancel')],stdout=subprocess.PIPE,stderr=subprocess.PIPE,text=True)
    time.sleep(.1);child.send_signal(signal.SIGINT);out,err=child.communicate(timeout=10)
    assert child.returncode==130,(child.returncode,out,err)
    assert sha(large)==before and not (work/'cancel').exists()
    records.append(dict(command=['docx-core inspect large; SIGINT after 100ms'],exitCode=child.returncode,stdout=out,stderr=err))
elif a.case=='network':
    sample=fixture('images')/'images.docx'
    trace=logs/'network.strace'
    run(['strace','-f','-e','trace=network','-o',trace,binary,'inspect','--input',sample,'--output',work/'offline'],2)
    data=trace.read_text()
    assert 'AF_INET' not in data and 'AF_INET6' not in data and 'connect(' not in data,data
    records.append(dict(assertion='No IPv4/IPv6 socket or connect syscall during external-image/hyperlink inspection in network-restricted execution',trace=str(trace)))
    probe=subprocess.run(['unshare','--user','--map-root-user','--net','true'],capture_output=True,text=True)
    records.append(dict(command=['unshare','--user','--map-root-user','--net','true'],exitCode=probe.returncode,stderr=probe.stderr,status='NT isolation' if probe.returncode else 'OK'))
    if probe.returncode==0:run(['unshare','--user','--map-root-user','--net',binary,'inspect','--input',sample,'--output',work/'disconnected'],2)
elif a.case=='resources':
    supervisor=root/'tools/run-isolated.py'
    run(['python3',supervisor,str(binary),'inspect','--input',small,'--output',work/'supervised'],2)
    run(['python3',supervisor,'--seconds','0.05','python3','-c','import time;time.sleep(2)'],75)
    run(['python3',supervisor,'--rss-mib','32','python3','-c','import time;x=bytearray(80*1024*1024);time.sleep(2)'],75)
elif a.case=='measurements':
    measures=[]
    for size in ['small','medium','large']:
        input=fixture('measure-'+size);output=work/size;stats=work/(size+'.time')
        before=sha(input)
        run(['/usr/bin/time','-f','%e %U %S %M','-o',stats,binary,'inspect','--input',input,'--output',output],2)
        wall,user,system,rss=map(float,stats.read_text().splitlines()[-1].split())
        assert sha(input)==before
        api=json.loads((input.parent/'measure-api.json').read_text())
        api.update(size=size,cliWallSeconds=wall,cliUserSeconds=user,cliSystemSeconds=system,cliPeakRssKiB=int(rss),
            outputBytes=sum(f.stat().st_size for f in output.rglob('*') if f.is_file()),output=str(output))
        measures.append(api)
    (logs/'measurements.json').write_text(json.dumps(measures,indent=2))
    print(json.dumps(measures,indent=2))
elif a.case=='packaging':
    run(['env','-u','DOTNET_ROOT','-u','DOTNET_ROOT_X64','PATH=/usr/bin:/bin',binary,'capabilities'],0)
    sizes=sum(f.stat().st_size for f in binary.parent.rglob('*') if f.is_file())
    records.append(dict(publishBytes=sizes,executableBytes=binary.stat().st_size,runtime=json.loads((binary.parent/'docx-core.runtimeconfig.json').read_text()),nativeAot='NT: clang absent; dependency AOT qualification not established'))
(logs/(a.case+('-commands' if a.case=='measurements' else '')+'.json')).write_text(json.dumps(records,indent=2))
print('OK',a.case,'| artifacts:',work)
