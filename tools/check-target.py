#!/usr/bin/env python3
"""Runs exactly one named DOCX-CORE-001A check. No aggregate/full-suite entry point."""
import argparse, datetime, json, pathlib, subprocess, sys
p=argparse.ArgumentParser()
p.add_argument('--case',required=True)
p.add_argument('--component',choices=['core','viewer'],default='core')
a=p.parse_args()
root=pathlib.Path(__file__).resolve().parents[1]
component='DocxCore.Targeted' if a.component=='core' else 'Viewer.Targeted'
command=[str(root/'.local/dotnet/dotnet'),str(root/f'tests/{component}/bin/Release/net10.0/{component}.dll'),'--case',a.case]
r=subprocess.run(command,cwd=root,capture_output=True,text=True,timeout=90)
out=root/'.local/qualification';out.mkdir(exist_ok=True)
with (out/'targeted.jsonl').open('a') as f:
    f.write(json.dumps({'case':a.case,'component':a.component,'command':command,'exitCode':r.returncode,'stdout':r.stdout,'stderr':r.stderr})+'\n')
(out/(a.case+'.log')).write_text(r.stdout+r.stderr)
print(r.stdout+r.stderr,end='')
sys.exit(r.returncode)
